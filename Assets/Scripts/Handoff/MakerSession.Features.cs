using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using UnityEngine;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // F59 (2026-11-13, the user: "if we make a lamp, where does the light go - the small details; sometimes it needs
    // sculpting too - our app doesn't have that"): a made model gets the working parts its kind needs, cut exactly
    // into its rebuilt solid (SolidRebuild operations), instead of a whole new Meshy run:
    //  - a lamp: hollow with 1.6 mm walls (thin enough for light to come through white PLA), open at the bottom for an
    //    LED puck, and a cable notch at the bottom rim; once cut it glows;
    //  - a planter: hollow, open at the top, with a drain hole;
    //  - a cup, vase, pen holder, bin, bowl or box: hollow, open at the top;
    //  - anything: "make a hole here" (where the fingertip touches it, else where the person looks), "hollow it".
    // After a make Fixer offers them (a teammate question, so Maker's Keep/Change choice waits behind it, F54) with
    // see-through parts on the thing; the notch and holes can be pinched and dragged where they should go. [Cut it]
    // cuts, [No thanks] leaves it; "undo the cut" takes the last cut back; "light on/off" for a lamp.
    // The cuts live in the design (`CoasterDesign.ops`, positions 0-1 across its bounds), so they survive versions
    // of the same size, a restart (restored made things are rebuilt) and printing (MakerSession.Solid.cs).
    public sealed partial class MakerSession
    {
        public enum FeatureSet { None, Lamp, Planter, Vessel }
        public static float LampWallMm = 1.6f, VesselWallMm = 2.2f, NotchWidthMm = 9f, NotchHeightMm = 7f, DrainMm = 6f, HoleMm = 8f;
        public static float GhostGrab = .03f;

        private sealed class Ghost
        {
            public string kind;              // open | notch | drain | hole | led
            public Transform view;
            public SolidRebuild.Op op;
            public Hand hand;                 // dragging with
            public bool mouse;
        }

        private readonly List<Ghost> ghosts = new List<Ghost>();
        private Transform ghostsOf;
        private FeatureSet ghostSet;
        private readonly HashSet<Transform> featuresOffered = new HashSet<Transform>();
        private readonly HashSet<string> appQuestions = new HashSet<string>();      // teammate questions the app answers itself
        private readonly Dictionary<Transform, Stack<SolidRebuild.Op[]>> cutHistory = new Dictionary<Transform, Stack<SolidRebuild.Op[]>>();
        private readonly HashSet<Transform> lit = new HashSet<Transform>();
        private readonly Dictionary<Transform, Material> glowLooks = new Dictionary<Transform, Material>();
        private readonly Dictionary<Transform, Transform> leds = new Dictionary<Transform, Transform>();
        private Material ghostLook;
        private bool cutting;

        public int FeatureCuts { get; private set; }
        public string[] GhostKinds => ghosts.Select(g => g.kind).ToArray();
        public bool LampLit(Transform made) => made != null && lit.Contains(made);
        public bool Cutting => cutting;

        public static FeatureSet FeatureSetFor(string words)
        {
            string p = (words ?? "").ToLowerInvariant();
            if (new[] { "lamp", "light", "lantern", "lampshade", "shade", "night light", "nightlight", "조명", "램프", "무드등", "등갓", "전등" }.Any(p.Contains)) return FeatureSet.Lamp;
            if (new[] { "planter", "plant pot", "flower pot", "flowerpot", "succulent", "화분" }.Any(p.Contains)) return FeatureSet.Planter;
            if (new[] { "cup", "mug", "vase", "pen holder", "pencil holder", "brush holder", "pen cup", "organizer", "organiser", "bin", "bowl", "box", "container", "jar",
                        "pot", "컵", "꽃병", "연필꽂이", "펜꽂이", "통", "그릇", "상자", "보관함" }.Any(p.Contains)) return FeatureSet.Vessel;
            return FeatureSet.None;
        }

        private static bool HasCuts(CoasterDesign d) => d?.ops != null && d.ops.Any(o => o != null && o.kind != "dab");

        // ------------------------------------------------------------ Fixer offers them after a make

        // From AfterMake: once Fixer's print check is out, the parts this kind needs.
        // F81 (Quest 2026-11-14: "while making, Fixer keeps asking 'cut the hole?' - what is that, why ruin my heart?"):
        // Fixer no longer offers cuts after a make; the parts are made only when the person asks ("make a hole here",
        // "hollow it", "drain hole", SolidVoice). OfferFeaturesUnasked = true brings the F59 offer back.
        public static bool OfferFeaturesUnasked;
        private void OfferFeaturesLater()
        {
            if (!OfferFeaturesUnasked) return;
            if (Product == null || Design == null || Design.kind != "model" || featuresOffered.Contains(Product) || HasCuts(Design)) return;
            var set = FeatureSetFor(Need + " " + Design.name);
            if (set == FeatureSet.None) return;
            featuresOffered.Add(Product);
            StartCoroutine(OfferWhenQuiet(Product, set));
        }

        private IEnumerator OfferWhenQuiet(Transform made, FeatureSet set)
        {
            float until = Time.time + 20f;
            yield return new WaitForSeconds(2f);
            while (Time.time < until && (crewBusy || lines.Count > 0 || Modelling || Sculpt != SculptStage.None)) yield return null;
            if (made == null || made != Product || Current != Step.Done || Sculpting) yield break;
            OfferFeatures(made, set);
        }

        private void OfferFeatures(Transform made, FeatureSet set)
        {
            long h = HitchStart();
            try { OfferFeaturesNow(made, set); } finally { HitchEnd("cut offer", h); }
        }

        private void OfferFeaturesNow(Transform made, FeatureSet set)
        {
            ClearGhosts();
            if (!LocalBox(made, out var min, out var size)) return;
            ghostsOf = made;
            ghostSet = set;
            Vector3 away = AwayFromPerson(made);
            switch (set)
            {
                case FeatureSet.Lamp:
                    AddGhost(made, "open", new SolidRebuild.Op { kind = "hollow", a = LampWallMm, side = "bottom" }, min, size);
                    AddGhost(made, "notch", new SolidRebuild.Op { kind = "notch", a = NotchWidthMm, b = NotchHeightMm, at = RimAt(away, min, size), dir = new[] { away.x, 0f, away.z } }, min, size);
                    AddGhost(made, "led", null, min, size);
                    Ask(Crew.Fixer, "For the light: hollow inside, open at the bottom, a notch for the cable. Drag the notch where you like.");
                    break;
                case FeatureSet.Planter:
                    AddGhost(made, "open", new SolidRebuild.Op { kind = "hollow", a = VesselWallMm, side = "top" }, min, size);
                    AddGhost(made, "drain", new SolidRebuild.Op { kind = "hole", a = DrainMm, b = 14f, at = new[] { .5f, 0f, .5f }, dir = new[] { 0f, 1f, 0f } }, min, size);
                    Ask(Crew.Fixer, "A planter needs a hollow inside and a drain hole. Drag the hole if you like.");
                    break;
                default:
                    AddGhost(made, "open", new SolidRebuild.Op { kind = "hollow", a = VesselWallMm, side = "top" }, min, size);
                    Ask(Crew.Fixer, "Hollow it out so it can hold things?");
                    break;
            }
            Body(Crew.Fixer)?.LookAtPoint(made.position + Vector3.up * .03f, 3f);
            ToySounds.Play(ToySounds.Cue.Scan, made.position, .35f, 1.2f);

            void Ask(Crew who, string text)
            {
                appQuestions.Add(text);
                AskWith(who, text, true, Rp("Cut it", "cut it in"), Rp("No thanks", "no cuts"));
            }
        }

        // A teammate question the app answers itself (MakerSession.Questions.AnswerWith asks here before the crew AI).
        private bool AppAnswer(OpenQuestion q, string say)
        {
            if (ResizeAnswered(q, say)) return true; // F88: Fixer's auto-resize question
            if (q == null || !appQuestions.Contains(q.text)) return false;
            appQuestions.Remove(q.text);
            Heard();
            Talk.Add("Person (tapped, answering " + MakerRoles.Name(q.who) + "): " + say);
            if (say == "cut it in") CutGhosts();
            else { ClearGhosts(); Say(Crew.Fixer, "Okay, solid it stays."); }
            return true;
        }

        // ------------------------------------------------------------ the see-through parts

        private void AddGhost(Transform made, string kind, SolidRebuild.Op op, Vector3 min, Vector3 size)
        {
            var paint = made.GetComponentInChildren<PaintableThing>();
            Transform parent = paint != null ? paint.transform : made;
            var g = new Ghost { kind = kind, op = op };
            g.view = new GameObject("Cut " + kind).transform;
            g.view.SetParent(parent, false);
            float big = Mathf.Max(size.x, size.z);
            switch (kind)
            {
                case "open":
                {
                    // a ring where it opens (bottom for a lamp, top for a vessel)
                    bool top = op.side == "top";
                    var ring = Prim(PrimitiveType.Cylinder, g.view, GhostColour(.25f));
                    ring.localScale = new Vector3(size.x * .78f, .0008f, size.z * .78f);
                    g.view.localPosition = new Vector3(min.x + size.x * .5f, top ? min.y + size.y + .0015f : min.y + .0015f, min.z + size.z * .5f);
                    break;
                }
                case "notch":
                {
                    var box = Prim(PrimitiveType.Cube, g.view, GhostColour(.55f));
                    box.localScale = new Vector3(NotchWidthMm * .001f, NotchHeightMm * .001f, Mathf.Max(.012f, big * .14f));
                    PlaceGhost(g, min, size);
                    break;
                }
                case "drain":
                case "hole":
                {
                    var cyl = Prim(PrimitiveType.Cylinder, g.view, GhostColour(.55f));
                    cyl.localScale = new Vector3(op.a * .001f, Mathf.Max(op.b, 8f) * .0005f, op.a * .001f);
                    PlaceGhost(g, min, size);
                    break;
                }
                case "led":
                {
                    var puck = Prim(PrimitiveType.Cylinder, g.view, LedLook());
                    puck.localScale = new Vector3(Mathf.Min(.04f, size.x * .45f), .004f, Mathf.Min(.04f, size.z * .45f));
                    g.view.localPosition = new Vector3(min.x + size.x * .5f, min.y + .004f, min.z + size.z * .5f);
                    break;
                }
            }
            StartCoroutine(GrowIn(g.view));
            ghosts.Add(g);
        }

        private Transform Prim(PrimitiveType type, Transform parent, Material look)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = look;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        private Material GhostColour(float fill)
        {
            if (ghostLook != null) return ghostLook;
            ghostLook = Tinted(new Color(.35f, .85f, 1f, 1f), fill);
            return ghostLook;
        }

        private Material ledLook;
        private Material LedLook()
        {
            if (ledLook != null) return ledLook;
            var template = TrueTemplate;
            ledLook = template != null ? new Material(template) : new Material(material);
            ledLook.name = "LED puck";
            if (ledLook.HasProperty("_BaseColor")) ledLook.SetColor("_BaseColor", new Color(1f, .93f, .78f));
            if (ledLook.HasProperty("_Glow")) ledLook.SetColor("_Glow", new Color(.6f, .45f, .2f));
            owned.Add(ledLook);
            return ledLook;
        }

        private static IEnumerator GrowIn(Transform t)
        {
            if (t == null) yield break;
            Vector3 full = t.localScale;
            for (float k = 0f; k < 1f && t != null; k += Time.deltaTime / .25f) { t.localScale = full * Mathf.SmoothStep(0f, 1f, k); yield return null; }
            if (t != null) t.localScale = full;
        }

        private void PlaceGhost(Ghost g, Vector3 min, Vector3 size)
        {
            if (g.view == null || g.op == null) return;
            Vector3 at = g.op.At(min, size), dir = g.op.Direction();
            if (g.kind == "notch")
            {
                dir.y = 0f;
                dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
                g.view.localPosition = new Vector3(at.x, min.y + NotchHeightMm * .0005f, at.z);
                g.view.localRotation = Quaternion.LookRotation(dir, Vector3.up);
            }
            else
            {
                g.view.localPosition = at;
                g.view.localRotation = Quaternion.FromToRotation(Vector3.up, dir);
            }
        }

        private void ClearGhosts()
        {
            foreach (var g in ghosts) if (g.view != null) Destroy(g.view.gameObject);
            ghosts.Clear();
            ghostsOf = null;
            ghostSet = FeatureSet.None;
        }

        // The made thing's box in its mesh's own space.
        private static bool LocalBox(Transform made, out Vector3 min, out Vector3 size)
        {
            min = size = Vector3.zero;
            var paint = made != null ? made.GetComponentInChildren<PaintableThing>() : null;
            var mesh = paint != null ? paint.Mesh : made != null ? made.GetComponentInChildren<MeshFilter>()?.sharedMesh : null;
            if (mesh == null) return false;
            var b = mesh.bounds;
            min = b.min;
            size = b.size;
            return size.x > 1e-4f && size.y > 1e-4f;
        }

        // Horizontal direction from the thing away from the person (a cable goes out the back), in its own space.
        private Vector3 AwayFromPerson(Transform made)
        {
            var paint = made.GetComponentInChildren<PaintableThing>();
            Transform frame = paint != null ? paint.transform : made;
            Vector3 world = Head != null ? made.position - Head.position : -made.forward;
            world.y = 0f;
            Vector3 local = frame.InverseTransformDirection(world.sqrMagnitude > 1e-6f ? world.normalized : Vector3.forward);
            local.y = 0f;
            return local.sqrMagnitude > 1e-6f ? local.normalized : Vector3.forward;
        }

        // A point on the bottom rim (the ellipse of the box) in direction `dir`, as 0-1 across the box.
        private static float[] RimAt(Vector3 dir, Vector3 min, Vector3 size)
        {
            dir.y = 0f;
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
            return new[] { .5f + dir.x * .5f, 0f, .5f + dir.z * .5f };
        }

        // ------------------------------------------------------------ dragging a notch or a hole

        // From OnPinch: a pinch on a see-through part picks it up (before the made thing would be grabbed).
        private bool FeaturePinch(Hand hand, Vector3 point, Ray? ray)
        {
            if (ghosts.Count == 0 || ghostsOf == null) return false;
            Ghost best = null;
            float bestDistance = GhostGrab;
            foreach (var g in ghosts)
            {
                if (g.view == null || g.op == null || g.kind == "open") continue;
                float d = Vector3.Distance(point, g.view.position);
                if (d < bestDistance) { bestDistance = d; best = g; }
                if (best == null && ray.HasValue)
                {
                    Vector3 toGhost = g.view.position - ray.Value.origin;
                    float along = Vector3.Dot(toGhost, ray.Value.direction);
                    if (along > .05f && Vector3.Cross(ray.Value.direction, toGhost).magnitude < .015f + along * .01f) best = g;
                }
            }
            if (best == null) return false;
            best.hand = hand;
            best.mouse = hand == null;
            ToySounds.Play(ToySounds.Cue.Press, best.view.position, .35f, 1.6f);
            return true;
        }

        private void FeaturesFrame()
        {
            if (ghosts.Count == 0) return;
            if (ghostsOf == null || ghostsOf != Product || Current != Step.Done) { ClearGhosts(); return; }
            if (!LocalBox(ghostsOf, out var min, out var size)) return;
            var paint = ghostsOf.GetComponentInChildren<PaintableThing>();
            Transform frame = paint != null ? paint.transform : ghostsOf;
            foreach (var g in ghosts)
            {
                if (g.hand == null && !g.mouse) continue;
                Vector3 world;
                if (g.hand != null)
                {
                    if (!HandIO.Pinching(g.hand) || !TryPinchPoint(g.hand, out world)) { g.hand = null; ToySounds.Play(ToySounds.Cue.Pop, g.view.position, .3f, 1.4f); continue; }
                }
                else continue; // (the Editor drags through SimulateGhostDrag)
                MoveGhost(g, frame.InverseTransformPoint(world), min, size);
            }
        }

        private void MoveGhost(Ghost g, Vector3 local, Vector3 min, Vector3 size)
        {
            Vector3 centre = min + size * .5f;
            if (g.kind == "notch")
            {
                Vector3 dir = local - centre;
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-8f) return;
                dir.Normalize();
                g.op.dir = new[] { dir.x, 0f, dir.z };
                g.op.at = RimAt(dir, min, size);
            }
            else
            {
                // a drain stays on the floor, inside the middle of the footprint; a hole slides over the surface
                if (g.kind == "drain")
                {
                    float x = Mathf.Clamp((local.x - min.x) / size.x, .2f, .8f), z = Mathf.Clamp((local.z - min.z) / size.z, .2f, .8f);
                    g.op.at = new[] { x, 0f, z };
                }
                else if (SurfaceNear(ghostsOf, local, out var onSurface, out var normal))
                {
                    g.op.at = SolidRebuild.Op.Rel(onSurface, min, size);
                    g.op.dir = new[] { normal.x, normal.y, normal.z };
                }
            }
            PlaceGhost(g, min, size);
        }

        private static bool TryPinchPoint(Hand hand, out Vector3 point)
        {
            point = Vector3.zero;
            if (!HandIO.Joint(hand, HandJointId.HandIndexTip, out var index) || !HandIO.Joint(hand, HandJointId.HandThumbTip, out var thumb)) return false;
            point = (index.position + thumb.position) * .5f;
            return true;
        }

        // The nearest point of the made thing's surface to a point in its own space, with the outward normal.
        private static bool SurfaceNear(Transform made, Vector3 local, out Vector3 point, out Vector3 normal)
        {
            point = local; normal = Vector3.up;
            var paint = made != null ? made.GetComponentInChildren<PaintableThing>() : null;
            if (paint == null) return false;
            Vector3 world = paint.transform.TransformPoint(local);
            if (paint.Closest(world, out var p, out var n, out _) < 0) return false;
            point = paint.transform.InverseTransformPoint(p);
            normal = paint.transform.InverseTransformDirection(n).normalized;
            // Closest turns the normal toward the query point; a point inside gives an inward normal
            var mesh = paint.Mesh;
            if (mesh != null && Vector3.Dot(normal, point - mesh.bounds.center) < 0f) normal = -normal;
            return true;
        }

        // ------------------------------------------------------------ cutting

        private void CutGhosts()
        {
            var made = ghostsOf;
            if (made == null || made != Product || Design == null) { ClearGhosts(); return; }
            var ops = ghosts.Where(g => g.op != null).Select(g => g.op.Copy()).ToList();
            bool lamp = ghostSet == FeatureSet.Lamp;
            ClearGhosts();
            if (ops.Count == 0) return;
            StartCoroutine(Cut(made, ops, lamp));
        }

        private IEnumerator Cut(Transform made, List<SolidRebuild.Op> ops, bool lamp)
        {
            if (cutting) yield break;
            cutting = true;
            working.Add(Crew.Maker);
            if (!BodyWork(Crew.Maker, "Work_Loop")) Cue(Crew.Maker, "thinking");
            Caption(Crew.Maker, "Cutting it…", 3f);
            var before = Design.ops;
            if (!cutHistory.TryGetValue(made, out var history)) cutHistory[made] = history = new Stack<SolidRebuild.Op[]>();
            history.Push(before);
            var all = new List<SolidRebuild.Op>();
            if (before != null) all.AddRange(before.Where(o => o != null && o.kind != "dab"));
            all.AddRange(ops);
            if (before != null) all.AddRange(before.Where(o => o != null && o.kind == "dab")); // the hand's shaping stays on top
            var design = Design.Copy();
            design.ops = all.ToArray();
            SolidRebuild.Job done = null;
            yield return ShowRebuilt(made, design, j => done = j);
            working.Remove(Crew.Maker);
            if (!BodyDone(Crew.Maker)) Cue(Crew.Maker, "encourage");
            cutting = false;
            if (made == null || done == null || done.error != null || done.result == null)
            {
                if (history.Count > 0) history.Pop();
                Say(Crew.Fixer, "That cut didn't work. Try a different spot?");
                yield break;
            }
            if (made == Product) Design = design;
            UpdateVersionDesign(made, design);
            FeatureCuts++;
            Note("design", "Cut: " + design.name, string.Join(", ", ops.Select(DescribeOp)));
            Talk.Add("Fixer cut it: " + string.Join(", ", ops.Select(DescribeOp)));
            ToyFx.Puff(made.position + Vector3.up * .02f, .012f, 5);
            ToySounds.Play(ToySounds.Cue.Chime, made.position, .6f, 1.25f);
            if (lamp) SetLit(made, true);
            Say(Crew.Fixer, lamp ? "Done. The thin walls let the light through: put a USB LED puck under it."
                : ops.Any(o => o.kind == "hole" && o.side == null && o.a <= DrainMm) && ops.Any(o => o.kind == "hollow") ? "Done: hollow, with a drain hole." : "Done.");
            Remember();
        }

        private static string DescribeOp(SolidRebuild.Op o) =>
            o.kind == "hollow" ? $"hollow, {o.a:0.#} mm walls, open {o.side}" : o.kind == "notch" ? $"cable notch {o.a:0} x {o.b:0} mm"
            : o.kind == "hole" ? $"hole {o.a:0} mm" : o.kind;

        private void UpdateVersionDesign(Transform made, CoasterDesign design)
        {
            var v = Versions.FirstOrDefault(x => x.root == made);
            if (v != null) v.design = design.Copy();
        }

        private IEnumerator UndoCut()
        {
            var made = Product;
            if (made == null || Design == null || !cutHistory.TryGetValue(made, out var history) || history.Count == 0)
            { Say(Crew.Fixer, "There's no cut to take back."); yield break; }
            var design = Design.Copy();
            design.ops = history.Pop();
            Caption(Crew.Maker, "Putting it back…", 2f);
            if (design.ops != null && design.ops.Length > 0) yield return ShowRebuilt(made, design, null);
            else yield return PlainView(made, design);
            if (made == Product) Design = design;
            UpdateVersionDesign(made, design);
            if (!HasCuts(design)) SetLit(made, false);
            Remember();
            Say(Crew.Fixer, "Back as it was.");
        }

        // The plain model again (no operations left).
        private IEnumerator PlainView(Transform made, CoasterDesign d)
        {
            var paint = made != null ? made.GetComponentInChildren<PaintableThing>() : null;
            if (paint == null) yield break;
            var marks = paint.SavedMarks();
            var mesh = CoasterMesh.Build(d);
            Paint(mesh, d.Tint(new Color(.95f, .85f, .62f)));
            owned.Add(mesh);
            var filter = paint.GetComponent<MeshFilter>();
            var renderer = paint.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = MadeMaterial(d);
            var thing = PaintableThing.Make(filter, renderer, mesh, FitShapes.ClassifierOf(d), FitShapes.PartsOf(d), d.kind == "model");
            ApplyMarks(made, marks);
            if (made == Product) ProductMesh = mesh;
            yield return null;
        }

        // ------------------------------------------------------------ the lamp's light

        private void SetLit(Transform made, bool on)
        {
            if (made == null) return;
            if (on) lit.Add(made); else lit.Remove(made);
            SolidLookChanged(made, made == Product ? Design : null);
            if (on) ToySounds.Play(ToySounds.Cue.Ding, made.position, .4f, 1.5f);
        }

        // After a view swap or a light switch: unpainted parts of a lit lamp glow warm, and its LED puck shows.
        private void SolidLookChanged(Transform made, CoasterDesign d)
        {
            var paint = made != null ? made.GetComponentInChildren<PaintableThing>() : null;
            if (paint == null || paint.Renderer == null) return;
            bool on = lit.Contains(made);
            var materials = paint.Renderer.sharedMaterials;
            var plain = d != null && HasCuts(d) ? SolidLook(d) : paint.Original(0);
            if (on && !glowLooks.ContainsKey(made) && SolidLook(d) is Material look && look.HasProperty("_Glow"))
            {
                var glow = new Material(look) { name = "Lit lamp" };
                glow.SetColor("_Glow", new Color(.42f, .3f, .14f));
                owned.Add(glow);
                glowLooks[made] = glow;
            }
            for (int i = 0; i < materials.Length; i++)
            {
                if (paint.Marks.ContainsKey(i)) continue;
                materials[i] = on && glowLooks.TryGetValue(made, out var g) ? g : plain != null ? plain : materials[i];
            }
            paint.Renderer.sharedMaterials = materials;
            Led(made, on);
        }

        private void Led(Transform made, bool on)
        {
            leds.TryGetValue(made, out var puck);
            if (!on) { if (puck != null) puck.gameObject.SetActive(false); return; }
            if (puck == null)
            {
                if (!LocalBox(made, out var min, out var size)) return;
                var paint = made.GetComponentInChildren<PaintableThing>();
                puck = new GameObject("LED puck").transform;
                puck.SetParent(paint != null ? paint.transform : made, false);
                var disc = Prim(PrimitiveType.Cylinder, puck, LedLook());
                disc.localScale = new Vector3(Mathf.Min(.04f, size.x * .45f), .004f, Mathf.Min(.04f, size.z * .45f));
                puck.localPosition = new Vector3(min.x + size.x * .5f, min.y + .002f, min.z + size.z * .5f);
                leds[made] = puck;
            }
            puck.gameObject.SetActive(true);
        }

        // ------------------------------------------------------------ words

        private bool FeatureVoice(string text, string p)
        {
            if (Product == null || Current != Step.Done || Design == null || Design.kind != "model" || Sculpting) return false;
            // the open offer answered in words
            if (ghosts.Count > 0 && Question != null && !Question.answered && appQuestions.Contains(Question.text))
            {
                if (IsYes(p) || p.Contains("cut it") || p.Contains("cut them") || p.Contains("do it") || p.Contains("잘라") || p.Contains("파 줘") || p.Contains("파줘"))
                { var q = Question; CloseQuestion("answered"); AppAnswer(q, "cut it in"); return true; }
                if (IsNo(p) || p.Contains("no thanks") || p.Contains("leave it") || p.Contains("괜찮아") || p.Contains("하지 마"))
                { var q = Question; CloseQuestion("answered"); AppAnswer(q, "no cuts"); return true; }
                if (ResizeGhost(p)) return true;
            }
            if (p.Contains("undo the cut") || p.Contains("undo cut") || p.Contains("put it back") || p.Contains("fill it back") || p.Contains("자른 거 취소") || p.Contains("다시 메워"))
            { StartCoroutine(UndoCut()); return true; }
            if (lit.Contains(Product) || HasCuts(Design) && FeatureSetFor(Need + " " + Design.name) == FeatureSet.Lamp)
            {
                if (p.Contains("light on") || p.Contains("turn it on") || p.Contains("switch it on") || p.Contains("불 켜") || p.Contains("켜 줘") || p.Contains("켜줘")) { SetLit(Product, true); return true; }
                if (p.Contains("light off") || p.Contains("turn it off") || p.Contains("switch it off") || p.Contains("불 꺼") || p.Contains("꺼 줘") || p.Contains("꺼줘")) { SetLit(Product, false); return true; }
            }
            bool hole = p.Contains("hole") || p.Contains("구멍");
            bool cable = p.Contains("cable") || p.Contains("cord") || p.Contains("wire") || p.Contains("전선") || p.Contains("케이블");
            bool drain = p.Contains("drain") || p.Contains("물 빠") || p.Contains("배수");
            bool hollow = p.Contains("hollow") || p.Contains("속을 비") || p.Contains("속 비") || p.Contains("파내");
            bool lampWords = p.Contains("where does the light") || p.Contains("where the light") || p.Contains("light go") || p.Contains("불 들어갈") || p.Contains("전구");
            if (!(hole || cable || drain || hollow || lampWords)) return false;
            Heard();
            if (!LocalBox(Product, out var min, out var size)) return false;
            ClearGhosts();
            ghostsOf = Product;
            if (lampWords) { featuresOffered.Add(Product); OfferFeatures(Product, FeatureSet.Lamp); return true; }
            if (cable)
            {
                Vector3 away = AwayFromPerson(Product);
                AddGhost(Product, "notch", new SolidRebuild.Op { kind = "notch", a = NotchWidthMm, b = NotchHeightMm, at = RimAt(away, min, size), dir = new[] { away.x, 0f, away.z } }, min, size);
                AskApp(Crew.Fixer, "A cable notch at the back. Drag it where you like.");
                return true;
            }
            if (drain)
            {
                AddGhost(Product, "drain", new SolidRebuild.Op { kind = "hole", a = DrainMm, b = 14f, at = new[] { .5f, 0f, .5f }, dir = new[] { 0f, 1f, 0f } }, min, size);
                AskApp(Crew.Fixer, "A drain hole in the bottom. Drag it if you like.");
                return true;
            }
            if (hollow && !hole)
            {
                bool lamp = FeatureSetFor(Need + " " + Design.name) == FeatureSet.Lamp;
                AddGhost(Product, "open", new SolidRebuild.Op { kind = "hollow", a = lamp ? LampWallMm : VesselWallMm, side = lamp || p.Contains("bottom") || p.Contains("아래") ? "bottom" : "top" }, min, size);
                ghostSet = lamp ? FeatureSet.Lamp : FeatureSet.Vessel;
                AskApp(Crew.Fixer, lamp ? "Hollow with thin walls, open at the bottom for the light?" : "Hollow, open at the top?");
                return true;
            }
            // a hole: where a fingertip touches it, else where the person looks at it, else the front
            Vector3 local = HoleSpot(Product, min, size, out Vector3 normal);
            float dia = SpokenMm(p) is float mm && mm >= 2f && mm <= 60f ? mm : HoleMm;
            AddGhost(Product, "hole", new SolidRebuild.Op { kind = "hole", a = dia, b = Mathf.Max(12f, Mathf.Min(size.x, size.z) * 1000f * .3f), at = SolidRebuild.Op.Rel(local, min, size), dir = new[] { normal.x, normal.y, normal.z } }, min, size);
            AskApp(Crew.Fixer, $"A {dia:0} mm hole here? Drag it to move it.");
            return true;
        }

        private void AskApp(Crew who, string text)
        {
            appQuestions.Add(text);
            AskWith(who, text, true, Rp("Cut it", "cut it in"), Rp("No thanks", "no cuts"));
        }

        private bool ResizeGhost(string p)
        {
            var g = ghosts.FirstOrDefault(x => x.kind == "hole" || x.kind == "drain" || x.kind == "notch");
            if (g == null) return false;
            float factor = p.Contains("bigger") || p.Contains("wider") || p.Contains("크게") || p.Contains("넓게") ? 1.35f : p.Contains("smaller") || p.Contains("narrower") || p.Contains("작게") || p.Contains("좁게") ? .75f : 0f;
            float? exact = SpokenMm(p);
            if (factor == 0f && exact == null) return false;
            g.op.a = Mathf.Clamp(exact ?? g.op.a * factor, 2f, 60f);
            if (g.kind == "notch") g.view.GetChild(0).localScale = new Vector3(g.op.a * .001f, g.op.b * .001f, g.view.GetChild(0).localScale.z);
            else g.view.GetChild(0).localScale = new Vector3(g.op.a * .001f, g.view.GetChild(0).localScale.y, g.op.a * .001f);
            Caption(Crew.Fixer, $"{g.op.a:0} mm", 2f);
            return true;
        }

        private static float? SpokenMm(string p)
        {
            var m = System.Text.RegularExpressions.Regex.Match(p, @"(\d+(?:\.\d+)?)\s*(mm|millimet|cm|centimet|밀리|센치|센티)");
            if (!m.Success || !float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) return null;
            return m.Groups[2].Value.StartsWith("c") || m.Groups[2].Value.StartsWith("센") ? v * 10f : v;
        }

        private Vector3 HoleSpot(Transform made, Vector3 min, Vector3 size, out Vector3 normal)
        {
            var paint = made.GetComponentInChildren<PaintableThing>();
            Transform frame = paint != null ? paint.transform : made;
            foreach (var side in new[] { Handedness.Right, Handedness.Left })
            {
                var hand = HandSet.Side(side);
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out var tip)) continue;
                if (paint != null && paint.Closest(tip.position, out _, out _, out float d) >= 0 && d < .05f &&
                    SurfaceNear(made, frame.InverseTransformPoint(tip.position), out var on, out normal)) return on;
            }
            if (Head != null && Physics.Raycast(new Ray(Head.position, Head.forward), out var hit, 3f) && hit.transform.IsChildOf(made) &&
                SurfaceNear(made, frame.InverseTransformPoint(hit.point), out var seen, out normal)) return seen;
            Vector3 front = -AwayFromPerson(made);
            Vector3 guess = min + size * .5f + Vector3.Scale(front, size * .5f);
            if (SurfaceNear(made, guess, out var onFront, out normal)) return onFront;
            normal = front;
            return guess;
        }

        // ------------------------------------------------------------ tests

        public void SimulateOfferFeatures(FeatureSet set) { if (Product != null) { featuresOffered.Add(Product); OfferFeatures(Product, set); } }
        public void SimulateGhostDrag(string kind, Vector3 world)
        {
            var g = ghosts.FirstOrDefault(x => x.kind == kind);
            if (g == null || ghostsOf == null || !LocalBox(ghostsOf, out var min, out var size)) return;
            var paint = ghostsOf.GetComponentInChildren<PaintableThing>();
            MoveGhost(g, (paint != null ? paint.transform : ghostsOf).InverseTransformPoint(world), min, size);
        }
        public SolidRebuild.Op GhostOp(string kind) => ghosts.FirstOrDefault(x => x.kind == kind)?.op;
        public Transform GhostView(string kind) => ghosts.FirstOrDefault(x => x.kind == kind)?.view;
        public void SimulateCut() => CutGhosts();
        public void SimulateUndoCut() => StartCoroutine(UndoCut());
    }
}
