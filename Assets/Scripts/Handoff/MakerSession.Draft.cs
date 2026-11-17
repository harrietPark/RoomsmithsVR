using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F40 (2026-11-12): v2 is drafted on a see-through twin.
    //  - After a make, a blue see-through copy of the made thing (same size and shape) appears beside it: v2.
    //  - Each teammate looks at the part its idea is about, says it in one line and stands its flag on the desk
    //    by itself. Then Maker says it, every time (in full the first time): these are only our thoughts; plant
    //    a flag on v2 for anything you want changed, or just say what you want; only planted flags change it.
    //  - Flags go anywhere, and where they are means something: on v2 = in the next version, at that spot; on
    //    the desk or in the air = set aside (it stays there); on its teammate's head = given back. Pinch one to
    //    pick it up again (near or by the hand's ray).
    //  - Saying something plants the person's own flag (where they pointed on v2, else where it fits). Sizes
    //    change on v2 itself at once (its W/D/H handles, words like "2 cm taller", two hands near it).
    //  - "Done": the planted flags light up and Maker reads them back ("I'll change these..."); flags can still be
    //    added or taken off; "yes" (or a thumbs up) is the one confirm, and v2 is made where the twin stands,
    //    beside v1. Only sizes: built at once from the same shape. Any other flag: the chosen picture is redrawn
    //    once with all of them (each with where it goes) and Meshy makes it once.
    public sealed partial class MakerSession
    {
        public enum FlagPlace { Offered, Held, Planted, Parked }
        public sealed class Flag
        {
            public Crew? who;                       // null: the person's own
            public string text, part;
            public FlagPlace place;
            public Vector3 sizeFactor = Vector3.one; // a size idea ("a bit taller"): what it does to v2
            public bool SizeOnly => sizeFactor != Vector3.one;
            public bool applied;                    // its size is on v2 now
            public Transform root, stand, halo, label;
            public MeshFilter cloth;
            public Vector3 local, normal;           // planted: where on v2 (the twin's mesh space)
            public Vector3 suggest;                 // the teammate's spot on v2 (mesh space)
            public bool hasSuggest;
            public Vector3 flyFrom;                 // F102: tapped, it hops from here into its spot
            public float flyAt = -1f;
        }

        public readonly List<Flag> Flags = new List<Flag>();
        public Transform Draft { get; private set; }
        public Vector3 DraftScale { get; private set; } = Vector3.one;
        public bool Confirming { get; private set; }
        public int DraftsMade { get; private set; }
        public string LastV2Plan { get; private set; }
        public Flag Carried => carried;
        public static float PlantReach = .03f;
        public const string DraftTaughtKey = "thinkmade.draftTaught";

        private Transform draftMesh, draftTag, plantRing;
        private Mesh draftSource;
        private Vector3[] draftVerts, draftNormals;
        private int[] draftTris;
        private CoasterDesign draftDesign, draftFrom, archiveDesign;
        private Vector3? versionSpot;
        private Quaternion versionTurn = Quaternion.identity;
        private Vector3 draftSizesMm;               // v2 sizes for ModelPlan (0 = unchanged)
        private Material hologram;
        private Flag carried;
        private Hand carryHand;
        private Vector3 carryOffset;
        private float carryFar = -1f;
        private bool carryMouse;
        private Plane carryPlane;
        private int explained;
        private float clipRebuildAt;
        private Vector3 clipBuiltScale = Vector3.one;

        // ------------------------------------------------------------ the twin

        private Material Hologram()
        {
            if (hologram != null) return hologram;
            var asset = Resources.Load<Material>("MakerHologram");
            hologram = asset != null ? new Material(asset) : new Material(ghostMaterial != null ? ghostMaterial : material);
            owned.Add(hologram);
            return hologram;
        }

        private void MakeDraft()
        {
            DestroyDraft();
            if (Product == null || ProductMesh == null || Design == null) return;
            draftDesign = Design.Copy();
            draftSource = ProductMesh;
            draftVerts = draftSource.vertices;
            draftNormals = draftSource.normals;
            draftTris = draftSource.triangles;
            if (draftNormals == null || draftNormals.Length != draftVerts.Length) { draftSource.RecalculateNormals(); draftNormals = draftSource.normals; }
            DraftScale = Vector3.one;
            clipBuiltScale = Vector3.one;
            Confirming = false;
            int next = Versions.Count > 0 ? Versions.Max(v => v.number) + 1 : 2;
            var root = new GameObject("Draft v" + next).transform;
            root.SetParent(transform, false);
            var go = new GameObject("Twin", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(root, false);
            go.GetComponent<MeshFilter>().sharedMesh = draftSource;
            var renderer = go.GetComponent<MeshRenderer>();
            var mat = Hologram();
            var sourceMaterial = MadeMaterial(draftDesign);
            var tex = sourceMaterial != null && sourceMaterial.HasProperty("_BaseMap") ? sourceMaterial.GetTexture("_BaseMap")
                : sourceMaterial != null && sourceMaterial.HasProperty("_MainTex") ? sourceMaterial.GetTexture("_MainTex") : null;
            if (tex != null && mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            draftMesh = go.transform;
            DraftPose(out Vector3 at, out Quaternion turn);
            root.SetPositionAndRotation(at, turn);
            draftTag = PaperTag("Draft Tag", .03f, .017f, Blueprint, out var text, 10f, false); // F77: a paper tag, not a pill
            text.text = "v" + next;
            text.color = Color.white;
            draftTag.SetParent(root, true);
            Draft = root;
            var collider = go.AddComponent<MeshCollider>();
            collider.sharedMesh = draftSource;
            collider.convex = true;
            var body = root.gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            DraftsMade++;
            root.localScale = Vector3.one * .2f;
            StartCoroutine(Grow(root, 1f, .35f));
            // F94: on the stand, the made one slips into < as the see-through one grows there.
            // F102 (Quest 2026-11-16, the user: "when I make v2, v1 on the plate should go - back with < > - and the v2
            // hologram should be ON the plate"): the made one had been moved off the stand, so the see-through one stood
            // beside it among the crew. Now, wherever the made one is, the see-through one stands on the project's stand
            // and the made one slips into < (and comes back with < standing on the stand).
            pagerShowsDraft = draftOnStand = false;
            if (DraftGoesOnStand)
            {
                pagerShowsDraft = draftOnStand = true;
                pagerBusyUntil = Time.time + .3f;
                if (held == Product) LetGo();
                bool onIt = StandsOn(Product, projectPlate);
                Quaternion restTurn = onIt ? Product.rotation : Upright(Product.rotation);
                Vector3 rest = onIt ? Product.position : OnStandPosition(Product, restTurn);
                StartCoroutine(SlipToArrow(Product, -1, false, rest, restTurn));
                if (!onIt) WriteInputLog("draft on the stand; the made one comes from where it was moved", true);
            }
            ToyFx.Puff(at + Vector3.up * draftSource.bounds.size.y * .5f, .012f, 4);
            ToySounds.Play(ToySounds.Cue.Pop, at, .45f, 1.5f);
            Talk.Add("A see-through v" + next + (draftOnStand ? " stands on the stand (the made one is behind <)" : " stands beside the made thing") + ": flags planted on it go into v" + next);
        }

        // F102: the see-through next version always goes onto the project's stand (F94 required the made one on it).
        public static bool DraftAlwaysOnStand = true;
        private bool DraftGoesOnStand => PlatePagerOn && projectPlate != null && projectPlate.gameObject.activeInHierarchy &&
                                         (DraftAlwaysOnStand || StandsOn(Product, projectPlate));

        // Where a thing stands on the project's stand turned `turn`: its footprint's middle on the top's middle.
        private Vector3 OnStandPosition(Transform thing, Quaternion turn)
        {
            var r = thing != null ? thing.GetComponentInChildren<Renderer>() : null;
            if (r == null || projectPlate == null) return thing != null ? thing.position : Vector3.zero;
            Quaternion keep = thing.rotation;
            thing.rotation = turn;
            var b = r.bounds;
            thing.rotation = keep;
            Vector3 top = StandTopOf(projectPlate);
            return thing.position + top - new Vector3(b.center.x, b.min.y, b.center.z);
        }

        private void DestroyDraft()
        {
            CancelCarry();
            if (Draft != null)
            {
                foreach (var f in Flags.Where(f => f.place == FlagPlace.Planted).ToList()) RemoveFlag(f, false);
                Destroy(Draft.gameObject);
            }
            Draft = null;
            draftMesh = null;
            draftTag = null;
            Confirming = false;
            DraftScale = Vector3.one;
            HideDraftDims();
            if (plantRing != null) plantRing.gameObject.SetActive(false);
        }

        // Beside v1 on the side away from the older versions (they step aside the other way); a clip: along its edge.
        private void DraftPose(out Vector3 at, out Quaternion turn)
        {
            turn = Product.rotation;
            // F94: with the stand's < >, the see-through one stands on the stand where the made one stands (that one
            // goes into <).
            if (DraftGoesOnStand)
            {
                // F102: wherever the made one was moved to, on the stand, upright.
                if (StandsOn(Product, projectPlate)) { at = Product.position; return; }
                turn = Upright(Product.rotation);
                at = OnStandPosition(Product, turn);
                return;
            }
            if (rails.TryGetValue(Product, out var rail))
            {
                // Along the same edge, on the side where the edge goes on (not off the end of the desk).
                Vector3 dir = rail.along.normalized;
                float offset = (Mathf.Max(20f, draftDesign.width) + 30f) * .001f;
                float now = Vector3.Dot(Product.position - rail.point, dir);
                Vector2 span = railSpan.TryGetValue(Product, out var known) ? known : new Vector2(-.6f, .6f);
                float s = now + offset <= span.y ? now + offset : now - offset >= span.x ? now - offset : Mathf.Clamp(now + offset, span.x, span.y);
                at = rail.point + dir * s;
                return;
            }
            var b = Product.GetComponentInChildren<MeshRenderer>().bounds;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - b.center, Vector3.up).normalized : Vector3.back;
            if (toUser.sqrMagnitude < .01f) toUser = Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, toUser);
            float radius = Mathf.Max(b.extents.x, b.extents.z);
            float step = radius * 2f + .045f;
            // Clear of everything on the desk: the things made (not v1 itself), the mic, the crew, the ring.
            var busy = Occupied().Where(o => Vector3.ProjectOnPlane(o.Item1 - Product.position, Vector3.up).magnitude > .01f).ToList();
            foreach (var c in cast.Values) if (c != null) busy.Add((c.transform.position, .07f));
            if (arrival.MicShown) busy.Add((arrival.MicPosition - Vector3.up * WindupsArrival.MicBase, .07f));
            if (arrival.TopicRing != null && arrival.TopicRing.enabled) busy.Add((arrival.TopicRing.transform.position, arrival.RingRadius + .03f));
            Vector3 best = Product.position + side * step;
            bool found = false;
            foreach (float k in new[] { 1f, -1f, 1.5f, -1.5f, 2f, -2f })
                foreach (float back in new[] { 0f, -.6f, .6f })
                {
                    if (found) break;
                    Vector3 p = Product.position + side * step * k + toUser * step * back;
                    // Off the working desk: the nearest desk point there, if that still stands clear of v1.
                    if (WorkSurface != null && !OnWorkDesk(new Vector3(p.x, WorkPoint(p).y, p.z), radius * .6f))
                    {
                        p = WorkPoint(p, radius + .01f);
                        if (Vector3.ProjectOnPlane(p - Product.position, Vector3.up).magnitude < step * .9f) continue;
                    }
                    if (busy.Any(o => Mathf.Abs(o.Item1.y - p.y) < .25f && Vector3.ProjectOnPlane(o.Item1 - p, Vector3.up).magnitude < o.Item2 + radius + .015f)) continue;
                    if (!DeskTop(p, out float y) || Mathf.Abs(y - Product.position.y) > .03f) continue;
                    best = new Vector3(p.x, y, p.z);
                    found = true;
                }
            if (!found)
            {
                // Nothing free: the side of v1 with more desk, never on v1 itself.
                Vector3 left = WorkPoint(Product.position - side * step, radius + .01f), right = WorkPoint(Product.position + side * step, radius + .01f);
                best = Vector3.Distance(left, Product.position) > Vector3.Distance(right, Product.position) ? left : right;
                if (Vector3.ProjectOnPlane(best - Product.position, Vector3.up).magnitude < step * .9f) best = Product.position + side * step;
            }
            at = best;
        }

        // The real desk top under a point (the room's colliders), else the MRUK surface.
        private bool DeskTop(Vector3 p, out float y)
        {
            int mask = arrival.handoff.meeting.environment.GeometryMask;
            if (Physics.Raycast(p + Vector3.up * .25f, Vector3.down, out var hit, .6f, mask, QueryTriggerInteraction.Ignore) && hit.normal.y > .9f) { y = hit.point.y; return true; }
            var surface = SurfaceAt(p + Vector3.up * .05f);
            y = surface != null ? surface.Point(new Vector2(p.x, p.z)).y : p.y;
            return surface != null;
        }

        private bool DraftIsClip => draftDesign != null && draftDesign.kind == "clip";

        // v1's sizes (mm): W across, D front to back, H up (the twin's own axes); a clip: its width, reach and drop.
        private Vector3 V1Size()
        {
            if (DraftIsClip) return new Vector3(draftDesign.width, draftDesign.rimHeight, draftDesign.grip);
            var s = draftSource != null ? draftSource.bounds.size * 1000f : Vector3.one * 80f;
            return new Vector3(s.x, s.y, s.z);
        }
        public Vector3 DraftSizeMm => Vector3.Scale(V1Size(), DraftScale);

        private void SetDraftScale(Vector3 scale)
        {
            DraftScale = new Vector3(Mathf.Clamp(scale.x, .3f, 3f), Mathf.Clamp(scale.y, .3f, 3f), Mathf.Clamp(scale.z, .3f, 3f));
            if (draftMesh != null && !DraftIsClip) draftMesh.localScale = DraftScale; // at once: a knob in the same frame reads it
            dimsUntil = Mathf.Max(dimsUntil, Time.time + 6f);
        }

        // ------------------------------------------------------------ flags

        // A teammate's idea for v2 (from its "made" turn): it looks at the spot, says it, and its flag stands by it.
        private void PlantFlag(Crew crew, string part, string text)
        {
            if (Product == null || Current != Step.Done || string.IsNullOrWhiteSpace(text)) return;
            if (Draft == null) MakeDraft();
            if (Draft == null) return;
            foreach (var old in Flags.Where(f => f.who == crew && f.place == FlagPlace.Offered).ToList()) RemoveFlag(old, false);
            var f = new Flag { who = crew, text = MakerRoles.Clean(text), part = part ?? "top", place = FlagPlace.Offered, sizeFactor = SizeFactorFor(text) };
            if (PartPointOnDraft(f.part, (int)crew, out var local, out _)) { f.suggest = local; f.hasSuggest = true; }
            BuildFlag(f);
            f.root.position = OfferSpot(crew);
            Flags.Add(f);
            StartCoroutine(Grow(f.root, 1f, .3f));
            // F97 (the user: "when the flags first appear, a sound or a particle effect (not a circle)"): a little
            // confetti pop from the flag's top, a pop and a chime, each flag a moment after the last.
            StartCoroutine(FlagPop(f, FlagPopDelay * Flags.Count(o => o.place == FlagPlace.Offered && o != f)));
            FlagPops++;
            var body = Body(crew);
            if (f.hasSuggest) body?.LookAtPoint(draftMesh.TransformPoint(f.suggest), 2.5f);
            body?.Play("HandRaise");
        }

        public const float FlagPopDelay = .18f;
        public int FlagPops { get; private set; }
        private IEnumerator FlagPop(Flag f, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            if (f == null || f.root == null) yield break;
            Vector3 top = f.root.TransformPoint(new Vector3(.0125f, .045f, 0f));
            ToyFx.Confetti(top, 10);
            ToySounds.Play(ToySounds.Cue.Pop, top, .55f, 1.5f);
            ToySounds.Play(ToySounds.Cue.Chime, top, .3f, 1.25f + .08f * ((int)(f.who ?? Crew.Maker)));
        }

        // On the desk between the teammate and v2, toward v2.
        // F97 (the user: "the flags are hard to grab; I keep grabbing the character - put them a bit farther from it"):
        // they stood 7 cm from their teammate, inside the teammate's own hand grab. Now FlagGap (15 cm) out, at least
        // FlagClear from every teammate, and FlagApart from each other.
        public const float FlagGap = .15f, FlagClear = .12f, FlagApart = .05f;
        private Vector3 OfferSpot(Crew crew)
        {
            Vector3 centre = DraftCentre();
            if (!cast.TryGetValue(crew, out var c) || c == null) return centre + Vector3.right * .08f;
            Vector3 from = c.transform.position;
            Vector3 toward = Vector3.ProjectOnPlane(centre - from, Vector3.up);
            float gap = toward.magnitude;
            Vector3 way = gap > 1e-3f ? toward / gap : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, way);
            Vector3 p = from + way * Mathf.Min(FlagGap, gap * .7f);
            var stand = Flags.Where(o => o.place == FlagPlace.Offered && o.root != null).Select(o => o.root.position).ToList();
            bool Crowded(Vector3 q)
            {
                foreach (var s in stand) if (Vector3.ProjectOnPlane(s - q, Vector3.up).magnitude < FlagApart) return true;
                foreach (var other in cast.Values)
                    if (other != null && Vector3.ProjectOnPlane(other.transform.position - q, Vector3.up).magnitude < FlagClear) return true;
                return false;
            }
            Vector3 start = p;
            for (int i = 1; i <= 8 && Crowded(p); i++) p = start + side * (FlagApart * ((i + 1) / 2) * (i % 2 == 0 ? -1f : 1f));
            if (Crowded(p)) p = start;
            return new Vector3(p.x, DeskTop(p, out float y) ? y : DeskHeightAt(p, from.y), p.z);
        }

        private Vector3 DraftCentre()
        {
            if (draftMesh == null) return Product != null ? Product.position : MugSpot;
            return draftMesh.GetComponent<MeshRenderer>().bounds.center;
        }

        private void BuildFlag(Flag f)
        {
            Color colour = f.who.HasValue ? MakerRoles.Color(f.who.Value) : new Color(1f, .58f, .26f);
            string key = f.who.HasValue ? f.who.Value.ToString() : "You";
            var root = new GameObject("Flag " + key).transform;
            root.SetParent(transform, false);
            Color white = new Color(.97f, .97f, .95f), dark = new Color(.15f, .15f, .17f), gold = new Color(1f, .83f, .25f);
            Transform Child(string name, Mesh mesh, Vector3 at, Transform parent = null)
            {
                var p = Part(name, mesh);
                p.SetParent(parent ?? root, false);
                p.localPosition = at;
                return p;
            }
            f.stand = Child("Stand", StickerMesh("flagStand", () => ToyMeshes.Disc(.0095f, .003f, new Color(.66f, .48f, .31f), new Color(.5f, .36f, .22f), "FlagStand")), new Vector3(0f, .0015f, 0f));
            f.stand.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Child("Pole", StickerMesh("flagPole", () => ToyMeshes.RoundedSlab(.0018f, .05f, .0008f, .0018f, white, new Color(.7f, .7f, .68f), white, "FlagPole")), new Vector3(0f, .025f, 0f));
            f.cloth = Child("Cloth", StickerMesh("flagCloth" + key, () => ToyMeshes.RoundedSlab(.024f, .016f, .002f, .002f, colour, Color.Lerp(colour, Color.black, .25f), colour, "FlagCloth")), new Vector3(.0125f, .042f, 0f)).GetComponent<MeshFilter>();
            var face = Child("Face", StickerMesh("flagFace", () => ToyMeshes.Disc(.0045f, .0006f, white, white, "FlagFace")), new Vector3(.0125f, .042f, -.0014f));
            if (f.who.HasValue)
            {
                Child("Eye", StickerMesh("flagEye", () => ToyMeshes.Disc(.0009f, .0004f, dark, dark, "FlagEye")), new Vector3(-.0016f, .0004f, -.0004f), face);
                Child("Eye", StickerMesh("flagEye", () => ToyMeshes.Disc(.0009f, .0004f, dark, dark, "FlagEye")), new Vector3(.0016f, .0004f, -.0004f), face);
            }
            else Child("Star", StickerMesh("flagStar", () => ToyMeshes.Disc(.0018f, .0004f, colour, colour, "FlagStar")), new Vector3(0f, 0f, -.0004f), face);
            // The outline that lights up when Maker reads the planted flags back.
            f.halo = new GameObject("Halo").transform;
            f.halo.SetParent(root, false);
            Child("Halo Cloth", StickerMesh("flagHaloCloth", () => ToyMeshes.RoundedSlab(.031f, .023f, .004f, .0011f, gold, gold, gold, "FlagHaloCloth")), new Vector3(.0125f, .042f, 0f), f.halo);
            Child("Halo Pole", StickerMesh("flagHaloPole", () => ToyMeshes.RoundedSlab(.0044f, .054f, .0021f, .0011f, gold, gold, gold, "FlagHaloPole")), new Vector3(0f, .025f, 0f), f.halo);
            f.halo.gameObject.SetActive(false);
            float width = Mathf.Clamp(.046f + f.text.Length * .0015f, .06f, .125f);
            f.label = PaperTag("Flag Label", width, .02f, Color.Lerp(colour, Color.white, .84f), out var t, 8.5f, false); // F77: words on the flag, not a pill
            t.text = f.text;
            f.label.SetParent(root, false);
            f.label.localPosition = new Vector3(0f, .067f, 0f);
            f.root = root;
        }

        // ------------------------------------------------------------ where a flag is

        private void RemoveFlag(Flag f, bool puff)
        {
            if (f == null) return;
            if (f.applied) Unapply(f);
            if (f.root != null)
            {
                if (puff) ToyFx.Puff(f.root.position + Vector3.up * .04f, .008f, 3);
                Destroy(f.root.gameObject);
            }
            Flags.Remove(f);
            if (carried == f) { carried = null; carryHand = null; carryMouse = false; }
        }

        private void ClearFlags(bool keepParked = false)
        {
            foreach (var f in Flags.ToList())
                if (!keepParked || f.place != FlagPlace.Parked) RemoveFlag(f, false);
        }

        private void Plant(Flag f, Vector3 local, Vector3 normal)
        {
            if (f == null || Draft == null) return;
            f.local = local;
            f.normal = normal;
            f.place = FlagPlace.Planted;
            f.root.SetParent(Draft, true);
            SetFlagLook(f);
            if (f.SizeOnly && !f.applied) { SetDraftScale(Vector3.Scale(DraftScale, f.sizeFactor)); f.applied = true; }
            PlaceFlags();
            ToySounds.Play(ToySounds.Cue.Press, f.root.position, .45f, 1.75f);
            ToyFx.Puff(f.root.position, .006f, 2);
            Talk.Add((f.who.HasValue ? MakerRoles.Name(f.who.Value) + "'s" : "The person's") + " flag planted on v2: " + f.text + " (" + Where(f) + ")");
            Heard();
        }

        private void Park(Flag f)
        {
            f.place = FlagPlace.Parked;
            f.root.SetParent(transform, true);
            Vector3 foot = f.root.position;
            var surface = SurfaceAt(foot + Vector3.up * .01f);
            if (surface != null)
            {
                float y = surface.Point(new Vector2(foot.x, foot.z)).y;
                if (foot.y - y < PlantReach && foot.y - y > -.02f) foot.y = y;
            }
            f.root.position = foot;
            SetFlagLook(f);
            ToySounds.Play(ToySounds.Cue.Press, foot, .3f, 1.3f);
            Talk.Add("Set aside: " + f.text);
        }

        private void GiveBack(Flag f)
        {
            var who = f.who.Value;
            Talk.Add("The person gave " + MakerRoles.Name(who) + "'s flag back: " + f.text);
            RemoveFlag(f, true);
            Body(who)?.Play("React_Nod");
            Caption(who, "Okay!", 1.4f);
        }

        private void Unapply(Flag f)
        {
            if (!f.applied) return;
            f.applied = false;
            SetDraftScale(new Vector3(DraftScale.x / f.sizeFactor.x, DraftScale.y / f.sizeFactor.y, DraftScale.z / f.sizeFactor.z));
        }

        // Offered and parked flags stand on a little round foot; on v2 they stick in; set aside they fade a little.
        private void SetFlagLook(Flag f)
        {
            if (f.root == null) return;
            bool standing = f.place == FlagPlace.Offered || (f.place == FlagPlace.Parked && SurfaceAt(f.root.position + Vector3.up * .01f) is var s && s != null &&
                                                                Mathf.Abs(s.Point(new Vector2(f.root.position.x, f.root.position.z)).y - f.root.position.y) < .004f);
            f.stand.gameObject.SetActive(standing);
            Color colour = f.who.HasValue ? MakerRoles.Color(f.who.Value) : new Color(1f, .58f, .26f);
            bool faded = f.place == FlagPlace.Parked;
            string key = (f.who.HasValue ? f.who.Value.ToString() : "You") + (faded ? "Faded" : "");
            Color c = faded ? Color.Lerp(colour, new Color(.85f, .85f, .85f), .55f) : colour;
            f.cloth.sharedMesh = StickerMesh("flagCloth" + key, () => ToyMeshes.RoundedSlab(.024f, .016f, .002f, .002f, c, Color.Lerp(c, Color.black, .25f), c, "FlagCloth"));
            f.halo.gameObject.SetActive(Confirming && f.place == FlagPlace.Planted);
        }

        // Every frame: planted flags stick out of v2's surface (it may have been resized or moved), the others
        // stand up; all face the person; while Maker reads them back, the planted ones glow.
        private void PlaceFlags()
        {
            foreach (var f in Flags)
            {
                if (f.root == null) continue;
                Vector3 up = Vector3.up;
                if (f.place == FlagPlace.Planted && draftMesh != null)
                {
                    f.root.position = draftMesh.TransformPoint(f.local);
                    if (f.flyAt >= 0f)
                    {
                        // F102: a tapped flag hops from where it stood into its spot.
                        float k = (Time.time - f.flyAt) / FlagFly;
                        if (k >= 1f) f.flyAt = -1f;
                        else f.root.position = Vector3.Lerp(f.flyFrom, f.root.position, Mathf.SmoothStep(0f, 1f, k)) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * .05f);
                    }
                    Vector3 n = draftMesh.rotation * new Vector3(f.normal.x / DraftScale.x, f.normal.y / DraftScale.y, f.normal.z / DraftScale.z);
                    up = Vector3.Slerp(Vector3.up, n.sqrMagnitude > 1e-8f ? n.normalized : Vector3.up, .55f).normalized;
                }
                Vector3 away = Head != null ? f.root.position - Head.position : Vector3.forward;
                Vector3 forward = Vector3.ProjectOnPlane(away, up);
                if (forward.sqrMagnitude < 1e-6f) forward = Vector3.ProjectOnPlane(Vector3.forward, up);
                f.root.rotation = Quaternion.LookRotation(forward.normalized, up);
                f.root.localScale = f.root.localScale.x < .99f ? f.root.localScale : Vector3.one;
                if (f.label != null) f.label.rotation = SpeechBubbleDock.FacingAt(f.label.position);
                bool glow = Confirming && f.place == FlagPlace.Planted;
                if (f.halo.gameObject.activeSelf != glow) f.halo.gameObject.SetActive(glow);
                if (glow) f.halo.localScale = Vector3.one * (1f + .05f * Mathf.Sin(Time.time * 7f));
            }
        }

        // Where a planted flag is on v2, in words (for the picture model and the notebook).
        private string Where(Flag f)
        {
            if (f.part == "whole shape") return "whole object silhouette";
            if (draftSource == null) return "on it";
            var b = draftSource.bounds;
            Vector3 q = new Vector3((f.local.x - b.min.x) / Mathf.Max(1e-5f, b.size.x), (f.local.y - b.min.y) / Mathf.Max(1e-5f, b.size.y), (f.local.z - b.min.z) / Mathf.Max(1e-5f, b.size.z));
            if (f.normal.y > .6f && q.y > .8f) return "on top" + Across(q.x);
            if (q.y < .14f) return "at the base" + Across(q.x);
            string height = q.y > .66f ? "upper" : q.y < .33f ? "lower" : "middle";
            string side = Mathf.Abs(f.normal.x) > Mathf.Abs(f.normal.z) ? (f.normal.x > 0f ? "right" : "left") : (f.normal.z > 0f ? "front" : "back");
            return height + " " + side + " side";
        }

        private static string Across(float x) => x < .3f ? ", left" : x > .7f ? ", right" : "";

        // A part's point on v2 (mesh space), then the nearest real vertex on its surface.
        private bool PartPointOnDraft(string part, int index, out Vector3 local, out Vector3 normal)
        {
            local = normal = Vector3.zero;
            if (draftSource == null || draftVerts == null || draftVerts.Length == 0) return false;
            var b = draftSource.bounds;
            float spread = (index - 1) * .25f;
            Vector3 p;
            switch (part)
            {
                case "base": p = new Vector3(b.center.x + b.extents.x * spread, b.min.y + b.size.y * .06f, b.max.z); break;
                case "side": p = new Vector3(b.max.x, b.center.y + b.extents.y * spread * .5f, b.center.z); break;
                case "inside": p = new Vector3(b.center.x + b.extents.x * spread * .5f, b.max.y - b.size.y * .1f, b.center.z); break;
                case "rim": p = new Vector3(b.center.x + b.extents.x * (spread + .6f) * .7f, b.max.y, b.center.z + b.extents.z * .6f); break;
                default: p = new Vector3(b.center.x + b.extents.x * spread, b.max.y, b.center.z); break;
            }
            if (draftTris == null || !NearestOnDraft(p, Vector3.one, out int tri, out local, out _)) return false;
            Vector3 v0 = draftVerts[draftTris[tri]], v1 = draftVerts[draftTris[tri + 1]], v2 = draftVerts[draftTris[tri + 2]];
            normal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
            if (Vector3.Dot(normal, draftNormals[draftTris[tri]] + draftNormals[draftTris[tri + 1]] + draftNormals[draftTris[tri + 2]]) < 0f) normal = -normal;
            return true;
        }

        // The nearest point of v2's surface to a world point (its triangles, as resized), and how far it is.
        // (Meshy's smart topology has big faces: the nearest vertex can be far from the nearest surface.)
        private bool DraftClosest(Vector3 world, out Vector3 local, out Vector3 normal, out float distance)
        {
            local = normal = Vector3.zero;
            distance = float.MaxValue;
            if (draftMesh == null || draftVerts == null || draftTris == null) return false;
            Vector3 s = DraftScale;
            // In the twin's resized space (mesh space times its scale), so distances are true.
            Vector3 q = Vector3.Scale(draftMesh.InverseTransformPoint(world), s);
            var b = draftSource.bounds;
            Vector3 lo = Vector3.Scale(b.min, s), hi = Vector3.Scale(b.max, s);
            if ((Vector3.Max(Vector3.zero, Vector3.Max(lo - q, q - hi))).magnitude > .1f) return false;
            if (!NearestOnDraft(q, s, out int bestTri, out Vector3 bestPoint, out float best)) return false;
            local = new Vector3(bestPoint.x / s.x, bestPoint.y / s.y, bestPoint.z / s.z);
            Vector3 v0 = draftVerts[draftTris[bestTri]], v1 = draftVerts[draftTris[bestTri + 1]], v2 = draftVerts[draftTris[bestTri + 2]];
            normal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
            // Face the way the vertex normals do (outward), whatever the winding.
            Vector3 vn = draftNormals[draftTris[bestTri]] + draftNormals[draftTris[bestTri + 1]] + draftNormals[draftTris[bestTri + 2]];
            if (Vector3.Dot(normal, vn) < 0f) normal = -normal;
            distance = Mathf.Sqrt(best) * (Draft != null ? Draft.lossyScale.x : 1f);
            return true;
        }

        // The nearest point on v2's triangles to q (both in mesh space times scale s).
        private bool NearestOnDraft(Vector3 q, Vector3 s, out int bestTri, out Vector3 bestPoint, out float best)
        {
            best = float.MaxValue;
            bestTri = -1;
            bestPoint = Vector3.zero;
            for (int i = 0; i + 2 < draftTris.Length; i += 3)
            {
                Vector3 a = Vector3.Scale(draftVerts[draftTris[i]], s), c1 = Vector3.Scale(draftVerts[draftTris[i + 1]], s), c2 = Vector3.Scale(draftVerts[draftTris[i + 2]], s);
                // Quick reject: the triangle's box is farther than the best so far.
                Vector3 tmin = Vector3.Min(a, Vector3.Min(c1, c2)), tmax = Vector3.Max(a, Vector3.Max(c1, c2));
                if ((Vector3.Max(Vector3.zero, Vector3.Max(tmin - q, q - tmax))).sqrMagnitude >= best) continue;
                Vector3 p = ClosestOnTriangle(q, a, c1, c2);
                float d = (p - q).sqrMagnitude;
                if (d >= best) continue;
                best = d;
                bestTri = i;
                bestPoint = p;
            }
            return bestTri >= 0;
        }

        // The closest point of a triangle to p (Ericson, Real-Time Collision Detection 5.1.5).
        private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        // Where a ray meets v2 (the person pointed at it), in mesh space.
        private bool DraftRayHit(Ray ray, out Vector3 local, out Vector3 normal)
        {
            local = normal = Vector3.zero;
            if (draftMesh == null || draftTris == null) return false;
            var inverse = draftMesh.worldToLocalMatrix;
            Vector3 o = inverse.MultiplyPoint3x4(ray.origin), d = inverse.MultiplyVector(ray.direction);
            float best = float.MaxValue;
            for (int i = 0; i + 2 < draftTris.Length; i += 3)
            {
                Vector3 a = draftVerts[draftTris[i]], e1 = draftVerts[draftTris[i + 1]] - a, e2 = draftVerts[draftTris[i + 2]] - a;
                Vector3 p = Vector3.Cross(d, e2);
                float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-12f) continue;
                Vector3 t0 = o - a;
                float u = Vector3.Dot(t0, p) / det;
                if (u < 0f || u > 1f) continue;
                Vector3 qv = Vector3.Cross(t0, e1);
                float v = Vector3.Dot(d, qv) / det;
                if (v < 0f || u + v > 1f) continue;
                float t = Vector3.Dot(e2, qv) / det;
                if (t <= 0f || t >= best) continue;
                best = t;
                local = o + d * t;
                normal = Vector3.Cross(e1, e2).normalized;
                if (Vector3.Dot(normal, d) > 0f) normal = -normal;
            }
            return best < float.MaxValue;
        }

        // ------------------------------------------------------------ picking up, carrying, letting go

        private static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
            return Vector3.Distance(p, a + ab * t);
        }

        public const float FlagReach = .055f; // (F97: 2.8 -> 4 cm; F102: 5.5 cm)
        private Flag FlagNear(Vector3 point, Ray? ray) => FlagNear(point, ray, out _);
        private Flag FlagNear(Vector3 point, Ray? ray, out float bestD)
        {
            Flag best = null;
            bestD = float.MaxValue;
            foreach (var f in Flags)
            {
                if (f.root == null || f == carried || !f.root.gameObject.activeInHierarchy) continue;
                Vector3 foot = f.root.position, top = f.root.TransformPoint(new Vector3(0f, .05f, 0f)), cloth = f.root.TransformPoint(new Vector3(.0125f, .042f, 0f));
                Vector3 label = f.label != null ? f.label.position : cloth;
                float d = Mathf.Min(SegmentDistance(point, foot, top), Mathf.Min(Vector3.Distance(point, cloth), Vector3.Distance(point, label)));
                if (d < FlagReach && d < bestD) { bestD = d; best = f; }
                if (ray.HasValue)
                {
                    float r = Mathf.Min(RayDistance(ray.Value, cloth), RayDistance(ray.Value, label));
                    if (r < .02f && r + .5f < bestD) { bestD = r + .5f; best = f; }
                }
            }
            return best;
        }

        // A pinch on a flag picks it up (near: it stays in the fingers; by the ray: it rides along the ray).
        private void PickFlag(Flag f, Hand hand, Vector3 point, Ray? ray)
        {
            if (f == null) return;
            Heard();
            bool near = SegmentDistance(point, f.root.position, f.root.TransformPoint(new Vector3(0f, .05f, 0f))) < .04f || Vector3.Distance(point, f.label != null ? f.label.position : f.root.position) < .04f;
            // F102: a short, still pinch is a tap (CarryFrame): it plants the flag at its own spot, or gives a planted
            // teammate's flag back.
            flagTapAt = Time.time;
            flagTapFrom = point;
            flagTapPlace = f.place;
            flagTapLive = FlagTaps && hand != null;
            carried = f;
            carryHand = hand;
            carryOffset = near ? f.root.position - point : Vector3.down * .03f;
            carryFar = !near && ray.HasValue ? Mathf.Max(.15f, Vector3.Dot(f.root.position - ray.Value.origin, ray.Value.direction)) : -1f;
            if (f.place == FlagPlace.Planted) Unapply(f);
            f.place = FlagPlace.Held;
            f.root.SetParent(transform, true);
            f.root.localScale = Vector3.one;
            SetFlagLook(f);
            ToySounds.Play(ToySounds.Cue.Paper, f.root.position, .4f, 1.4f);
        }

        // Tests (and VerifyMake3D): a flag goes straight onto v2 at the spot its teammate meant.
        public void TakeFlag(Flag f)
        {
            if (f == null || Draft == null || f.place == FlagPlace.Planted) return;
            Vector3 local = f.suggest, normal = Vector3.up;
            if (!f.hasSuggest && !PartPointOnDraft(f.part ?? "top", 1, out local, out normal)) return;
            if (f.hasSuggest) PartPointOnDraft(f.part ?? "top", f.who.HasValue ? (int)f.who.Value : 1, out local, out normal);
            Plant(f, local, normal);
        }

        // Tests and the mouse: pick a flag up as if pinched at its foot.
        public void SimulatePickFlag(Flag f) { PickFlag(f, null, f.root.position, null); carryMouse = true; }
        public void SimulateMoveFlag(Vector3 foot) { if (carried != null) MoveFlag(foot); }
        public void SimulateDropFlag() { if (carried != null) DropFlag(); }

        private void MoveFlag(Vector3 foot)
        {
            if (carried?.root == null) return;
            carried.root.position = foot;
            bool near = DraftClosest(foot, out var local, out var normal, out float d) && d < PlantReach;
            if (plantRing == null)
            {
                plantRing = Part("Plant Ring", RingMesh());
                plantRing.SetParent(transform, false);
            }
            plantRing.gameObject.SetActive(near);
            if (!near) return;
            Vector3 at = draftMesh.TransformPoint(local);
            Vector3 n = (draftMesh.rotation * normal).normalized;
            plantRing.SetPositionAndRotation(at + n * .001f, Quaternion.FromToRotation(Vector3.up, n));
            plantRing.localScale = Vector3.one * .008f;
        }

        private void CarryFrame(Hand hand, bool pinching, Vector3 point)
        {
            if (flagTapLive && hand != null)
            {
                bool still = Time.time - flagTapAt <= FlagTapTime && (float.IsInfinity(point.x) || Vector3.Distance(point, flagTapFrom) <= FlagTapMove);
                if (!HandIO.RawPinching(hand)) { if (still) { TapFlag(hand); return; } flagTapLive = false; }
                else if (!still) flagTapLive = false;
            }
            if (!pinching) { DropFlag(); return; }
            if (carryFar > 0f && hand != null && HandIO.Pointer(hand, out Pose pointer)) MoveFlag(pointer.position + pointer.forward * carryFar + carryOffset);
            else MoveFlag(point + carryOffset);
        }

        private void DropFlag()
        {
            var f = carried;
            carried = null;
            carryHand = null;
            carryMouse = false;
            if (plantRing != null) plantRing.gameObject.SetActive(false);
            if (f?.root == null) return;
            if (Draft != null && DraftClosest(f.root.position, out var local, out var normal, out float d) && d < PlantReach) Plant(f, local, normal);
            else if (f.who.HasValue && NearHead(f.who.Value, f.root.position)) GiveBack(f);
            else Park(f);
        }

        // F102 (Quest 2026-11-16, the user: "grabbing the flags is still too hard - any way?"): the log had pinches going
        // to v1 and v2 beside the flags, and flags held .6-1.2 s then let go where they were (parked). Now a short, still
        // pinch on a flag (near, or aimed from afar: flags are far Tap targets) is a tap: an offered or set-aside flag
        // hops into v2 at its own spot; a teammate's planted flag goes back to it. Dragging still puts one anywhere.
        // A flag takes a pinch before v1, v2 and the stand unless one of them is FlagWins nearer (FlagPinchFirst).
        public static bool FlagTaps = true;
        public const float FlagTapTime = .45f, FlagTapMove = .025f, FlagWins = .01f, FlagFly = .3f;
        private float flagTapAt = -10f;
        private Vector3 flagTapFrom;
        private FlagPlace flagTapPlace;
        private bool flagTapLive;
        public int FlagTapsDone { get; private set; }

        private void TapFlag(Hand hand)
        {
            var f = carried;
            flagTapLive = false;
            carried = null;
            carryHand = null;
            carryMouse = false;
            if (hand != null) HandIO.EndHold(hand);
            if (plantRing != null) plantRing.gameObject.SetActive(false);
            if (f?.root == null) return;
            TapFlag(f, flagTapPlace);
        }

        // Tests, the mouse and the far tap: as a tap on `f` that was `before` when touched.
        private void TapFlag(Flag f, FlagPlace before)
        {
            Vector3 from = f.root.position;
            FlagTapsDone++;
            if (before == FlagPlace.Planted)
            {
                if (f.who.HasValue) { WriteInputLog("flag tapped: " + MakerRoles.Name(f.who.Value) + "'s flag goes back", true); GiveBack(f); return; }
                Plant(f, f.local, f.normal); // the person's own flag stays where it was (dragging takes it off)
                return;
            }
            if (Draft == null) { Park(f); return; }
            TakeFlag(f);
            if (f.place != FlagPlace.Planted) { Park(f); return; }
            f.flyFrom = from;
            f.flyAt = Time.time;
            WriteInputLog("flag tapped: planted on v" + NextVersionNumber + " (" + Where(f) + ")", true);
        }

        public void SimulateTapFlag(Flag f) { if (f?.root != null) { var before = f.place; if (before == FlagPlace.Planted) Unapply(f); f.place = FlagPlace.Held; TapFlag(f, before); } }

        // OnPinch, before the nearest made thing: a flag within reach takes the pinch unless something is clearly nearer.
        private bool FlagPinchFirst(Hand hand, Vector3 point)
        {
            if (Flags.Count == 0 || float.IsInfinity(point.x)) return false;
            var f = FlagNear(point, null, out float d);
            if (f == null) return false;
            if (NearestGrab(point, out float other, out _) != GrabKind.None && other + FlagWins < d) return false;
            PickFlag(f, hand, point, null);
            return true;
        }
        public bool FlagWouldTake(Vector3 point) => Flags.Count > 0 && FlagNear(point, null, out float d) != null &&
            (NearestGrab(point, out float other, out _) == GrabKind.None || other + FlagWins >= d); // (checks)

        private void CancelCarry()
        {
            if (carried == null) return;
            var f = carried;
            carried = null;
            carryHand = null;
            carryMouse = false;
            if (f.root != null) Park(f);
        }

        private bool NearHead(Crew crew, Vector3 p)
        {
            var body = Body(crew);
            Vector3 head = body != null ? body.HeadTop - Vector3.up * .045f : cast.TryGetValue(crew, out var c) && c != null ? c.transform.position + Vector3.up * .11f : Vector3.positiveInfinity;
            return Vector3.Distance(p, head) < .08f;
        }

        // ------------------------------------------------------------ words

        // What the AI's (or the person's) words do to v2's size, when they are about size only.
        public static Vector3 SizeFactorFor(string text)
        {
            string p = (text ?? "").ToLowerInvariant();
            const float k = 1.15f;
            if (Regex.IsMatch(p, @"\b(taller|higher)\b") || p.Contains("더 높")) return new Vector3(1f, k, 1f);
            if (Regex.IsMatch(p, @"\b(shorter|lower)\b") || p.Contains("더 낮")) return new Vector3(1f, 1f / k, 1f);
            if (Regex.IsMatch(p, @"\b(wider|broader)\b") || p.Contains("더 넓")) return new Vector3(k, 1f, 1f);
            if (Regex.IsMatch(p, @"\b(narrower|slimmer)\b") || p.Contains("더 좁")) return new Vector3(1f / k, 1f, 1f);
            if (Regex.IsMatch(p, @"\b(deeper)\b")) return new Vector3(1f, 1f, k);
            if (Regex.IsMatch(p, @"\b(bigger|larger)\b") || p.Contains("더 크")) return Vector3.one * k;
            if (Regex.IsMatch(p, @"\b(smaller|tinier)\b") || p.Contains("더 작")) return Vector3.one / k;
            return Vector3.one;
        }

        // Size words on v2, applied at once: "12 cm tall", "2 cm wider", "height 90 mm", "taller", "bigger"...
        private bool DraftSizeWords(string p)
        {
            var axisOf = new Dictionary<string, int>
            {
                { "tall", 1 }, { "high", 1 }, { "height", 1 }, { "taller", 1 }, { "shorter", 1 }, { "높", 1 },
                { "wide", 0 }, { "width", 0 }, { "wider", 0 }, { "narrower", 0 }, { "폭", 0 }, { "가로", 0 }, { "넓", 0 }, { "좁", 0 },
                { "deep", 2 }, { "depth", 2 }, { "deeper", 2 }, { "깊", 2 }, { "세로", 2 }
            };
            int axis = -1;
            foreach (var pair in axisOf) if (p.Contains(pair.Key)) { axis = pair.Value; break; }
            var size = DraftClipAxis(DraftSide(axis)); // F88b: "wider" is the person's left-right, whichever way v2 is turned
            bool hasNumber = TryMillimetres(p, out float mm) || TryShortMillimetres(p, out mm);
            Vector3 now = DraftSizeMm;
            if (hasNumber)
            {
                bool relative = Regex.IsMatch(p, @"\b(more|taller|wider|deeper|longer|shorter|narrower|less)\b") || p.Contains("더");
                bool less = Regex.IsMatch(p, @"\b(shorter|narrower|less|smaller)\b") || p.Contains("줄") || p.Contains("작게") || p.Contains("낮");
                if (axis < 0)
                {
                    // A size with no axis: the key size, keeping the shape.
                    int key = KeyAxis == "H" ? 1 : DraftSide(0);
                    float target = relative ? now[key] + (less ? -mm : mm) : mm;
                    SetDraftScale(DraftScale * (target / Mathf.Max(1f, now[key])));
                    AnnounceDraftSize(key);
                    return true;
                }
                float goal = relative ? now[size] + (less ? -mm : mm) : mm;
                var scale = DraftScale;
                scale[size] = goal / Mathf.Max(1f, V1Size()[size]);
                SetDraftScale(scale);
                AnnounceDraftSize(size);
                return true;
            }
            var factor = SizeFactorFor(p);
            if (factor == Vector3.one || p.Split(' ').Length > 6) return false;
            if (DraftAcross) factor = new Vector3(factor.z, factor.y, factor.x); // F88b: the person's width is v2's own z
            SetDraftScale(Vector3.Scale(DraftScale, factor));
            AnnounceDraftSize(factor.y != 1f && factor.x == 1f ? 1 : factor.z != 1f && factor.x == 1f ? 2 : 0);
            return true;
        }

        // A clip's sizes are its width (W), drop (H) and reach over the desk (D): the same three handles.
        private static int DraftClipAxis(int axis) => axis < 0 ? 0 : axis;

        private static bool TryShortMillimetres(string p, out float mm)
        {
            mm = 0f;
            var m = Regex.Match(p, @"(\d+(?:[.,]\d+)?)\s*(mm|millimet|밀리|cm|centimet|센티)");
            if (!m.Success) return false;
            float value = float.Parse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            mm = m.Groups[2].Value.StartsWith("c") || m.Groups[2].Value.StartsWith("센") ? value * 10f : value;
            return mm > 0f && mm <= 2000f;
        }

        private void AnnounceDraftSize(int axis)
        {
            string[] names = DraftIsClip ? new[] { "Width", "Drop", "Reach" } : new[] { "Width", "Height", "Depth" };
            string name = names[DraftSide(axis)]; // F88b: `axis` is v2's own; named as the person sees it
            Ack(Crew.Fixer, name + " " + DraftSizeMm[axis].ToString("0") + " mm");
            Talk.Add("v2 resized: " + name + " " + DraftSizeMm[axis].ToString("0") + " mm");
            dimsUntil = Time.time + 8f;
        }

        // "Done" said on its own (or nearly), and the words that only count when they are all that was said.
        private static readonly string[] DraftDoneWords = { "done", "that's it", "thats it", "that's all", "i'm done", "im done", "finished", "다 했", "됐어", "끝", "완료", "다 됐" };
        private static readonly string[] DoneAlone = { "ready", "go", "make v2", "build v2", "make it", "build it", "make the next one", "만들어", "만들어줘", "만들자" };
        private static readonly string[] NoWords = { "no", "nope", "wait", "not yet", "cancel", "hold on", "stop", "아니", "잠깐", "아직", "취소" };

        public static bool SaysDone(string p) => DoneAlone.Contains(p) || (p.Split(' ').Length <= 5 && DraftDoneWords.Any(p.Contains));

        // Voice while v2 is drafted: the confirm, "done", sizes and "measure it". Plain sentences become the
        // person's own flags (StepInput); questions still go to the crew.
        // (F48) answering: a teammate's question is open, so a bare "yes" / "done" answers that and is not the v2 confirm.
        private bool DraftVoice(string text, string p, bool answering = false)
        {
            if (Draft == null || Current != Step.Done) return false;
            if (Confirming)
            {
                if (NoWords.Any(w => p == w || p.StartsWith(w + " ") || p.StartsWith(w + ","))) { EndConfirm(); Ack(Crew.Maker, "Okay, take your time."); return true; }
                if (IsYes(p) || MakeNow(p) || SaysDone(p)) { BuildFromDraft(); return true; }
            }
            if (p.Contains("measure it") || p.Contains("measure v2") || p.Contains("how big is it") || p.Contains("재줘") || p.Contains("재 줘") || p.Contains("크기 보여"))
            {
                MeasureDraft();
                return true;
            }
            if (DraftSizeWords(p)) return true;
            if (!answering && (SaysDone(p) || IsYes(p))) { StartConfirm(); return true; }
            return false;
        }

        // The person's own idea, as their flag on v2: where they pointed at it, else where it fits.
        private void PersonFlag(string text, string part = null, bool quiet = false)
        {
            if (Draft == null) return;
            PagerShowDraft(true); // F94: a change said while the stand shows a made version goes onto the see-through one
            AddShapeRule(text); // F63: "no bumps", "a flat bottom" hold for the rest of this make
            string clean = MakerRoles.Clean(text).Trim().TrimEnd('.', '!');
            clean = Regex.Replace(clean, @"^(please|can you|could you|i want|i'd like|let's|maybe)\s+", "", RegexOptions.IgnoreCase);
            if (clean.Length == 0) return;
            bool whole = GlobalShapeRequest(text);
            if (whole) { part = "whole shape"; ShapeRequirements = text; }
            // Keep the complete request; only visual labels may abbreviate it.
            var f = new Flag { who = null, text = char.ToUpperInvariant(clean[0]) + clean.Substring(1), part = part ?? PartFromWords(text.ToLowerInvariant()), place = FlagPlace.Held, sizeFactor = Vector3.one };
            BuildFlag(f);
            Flags.Add(f);
            Vector3 local = Vector3.zero, normal = Vector3.up;
            bool pointed = WindupsGestures.RecentPoint(5f, out Ray ray) && DraftRayHit(ray, out local, out normal);
            if (!pointed && !PartPointOnDraft(f.part, 1, out local, out normal)) { local = draftSource.bounds.center + Vector3.up * draftSource.bounds.extents.y; normal = Vector3.up; }
            f.root.position = draftMesh.TransformPoint(local);
            Plant(f, local, normal);
            StartCoroutine(Grow(f.root, 1f, .3f));
            if (!quiet) Ack(Crew.Maker, whole ? "Got it: the whole outline. Tap Done to make v" + NextVersionNumber + "." : "Planted! Tap Done when your changes are ready.");
            Body(Crew.Maker)?.LookAtPoint(f.root.position, 2f);
        }

        private static string PartFromWords(string p) =>
            p.Contains("top") || p.Contains("lid") || p.Contains("rim") || p.Contains("위") || p.Contains("윗") ? "top"
            : p.Contains("bottom") || p.Contains("base") || p.Contains("foot") || p.Contains("밑") || p.Contains("바닥") ? "base"
            : p.Contains("inside") || p.Contains("안쪽") ? "inside"
            : p.Contains("side") || p.Contains("handle") || p.Contains("옆") || p.Contains("손잡이") ? "side" : "top";

        // ------------------------------------------------------------ Maker explains (every time)

        private static readonly string[] ShortExplains =
        {
            "Plant what you want changed on v2, or just tell me.",
            "Only flags on v2 change it. Plant one, or say yours.",
            "Your call: plant flags on v2, or tell me. Say done when ready."
        };

        public void ExplainDraft()
        {
            if (Draft == null) return;
            if (FormFirst && !ShapeConfirmed) { ShowShapeChoices(); return; }
            explained++;
            // (No flags came from the crew this time: then only what the person says or pulls changes v2.)
            if (!Flags.Any(f => f.place == FlagPlace.Offered))
                Say(Crew.Maker, "That's v2. Tell me what to change, or pull its handles. Say done when ready.");
            else if (!Taught(DraftTaughtKey))
            {
                MarkTaught(DraftTaughtKey);
                Say(Crew.Maker, "These are just our thoughts. Plant a flag on v2 for anything you want changed.");
                AskWith(Crew.Maker, "Anything else? Plant a flag, or say done.", false, Rp("Done"));
            }
            else Say(Crew.Maker, ShortExplains[(explained - 1) % ShortExplains.Length]);
            Body(Crew.Maker)?.LookAtPoint(DraftCentre(), 2.5f);
            StartCoroutine(After(1.2f, PulseMic));
        }

        // ------------------------------------------------------------ done: read back, one confirm, make v2

        private bool DraftSized => (DraftScale - Vector3.one).magnitude > .006f;

        public void StartConfirm()
        {
            if (FormFirst && !revisionPrepared) { if (Review == ReviewStage.Shape) StartCoroutine(PreviewRevision()); return; }
            if (Draft == null) return;
            var planted = Flags.Where(f => f.place == FlagPlace.Planted).ToList();
            if (planted.Count == 0 && !DraftSized)
            {
                Say(Crew.Maker, "Nothing on v2 yet. Plant a flag on it, or tell me what to change.");
                return;
            }
            Confirming = true;
            foreach (var f in planted) SetFlagLook(f);
            dimsUntil = Time.time + 30f;
            var items = planted.Where(f => !f.SizeOnly).Select(f => f.text.TrimEnd('.')).ToList();
            if (DraftSized) items.Add(SizeWords());
            Hush();
            Say(Crew.Maker, "I'll change these: " + Join(items.Take(4).ToList()) + (items.Count > 4 ? ", and more" : "") + ".");
            Say(Crew.Maker, "Add or take any off now. Then say yes.");
            Body(Crew.Maker)?.Play("Decision_Present");
            Talk.Add("Maker read back the v2 plan and waits for a yes");
        }

        private void EndConfirm()
        {
            Confirming = false;
            foreach (var f in Flags) if (f.root != null) SetFlagLook(f);
        }

        private string SizeWords()
        {
            var now = DraftSizeMm;
            var v1 = V1Size();
            var parts = new List<string>();
            string[] names = DraftIsClip ? new[] { "wide", "drop", "reach" } : new[] { "wide", "tall", "deep" };
            for (int i = 0; i < 3; i++) if (Mathf.Abs(now[i] - v1[i]) > .5f) parts.Add(now[i].ToString("0") + " mm " + names[DraftSide(i)]); // F88b
            return parts.Count > 0 ? string.Join(", ", parts) : "the new size";
        }

        public void BuildFromDraft()
        {
            if (Draft == null || Design == null) return;
            if (FormFirst && !revisionPrepared) { if (Review == ReviewStage.Shape) StartCoroutine(PreviewRevision()); return; }
            EndConfirm();
            var planted = Flags.Where(f => f.place == FlagPlace.Planted).ToList();
            var shape = planted.Where(f => !f.SizeOnly).ToList();
            bool sized = DraftSized;
            if (shape.Count == 0 && !sized) { StartConfirm(); return; }
            int next = Versions.Count > 0 ? Versions.Max(v => v.number) + 1 : 2;
            string change = FormFirst ? revisionChange : string.Join(" ", shape.Select((f, i) => (i + 1) + ". " + f.text.TrimEnd('.') + " (" + Where(f) + ")."));
            LastV2Plan = change + (sized && !change.Contains("Size:") ? (change.Length > 0 ? " " : "") + "Size: " + SizeWords() + "." : ""); // (F77: the form-first plan already says its size)
            Note("decision", "v" + next + " plan", LastV2Plan);
            Talk.Add("The person confirmed v" + next + ": " + LastV2Plan);
            // The used flags go to Maker (into its notebook); offered ones the person left go back; set aside stays.
            if (!FormFirst)
            {
                foreach (var f in planted) StartCoroutine(FlyToMaker(f));
                foreach (var f in Flags.Where(f => f.place == FlagPlace.Offered).ToList()) RemoveFlag(f, true);
            }
            versionSpot = Draft.position;
            versionTurn = Draft.rotation;
            archiveDesign = Design.Copy();
            draftFrom = Design.Copy();
            Vector3 v1 = V1Size(), now = DraftSizeMm;
            draftSizesMm = sized ? new Vector3(Mathf.Abs(now.x - v1.x) > .5f ? now.x : 0f, Mathf.Abs(now.y - v1.y) > .5f ? now.y : 0f, Mathf.Abs(now.z - v1.z) > .5f ? now.z : 0f) : Vector3.zero;
            bool clip = DraftIsClip;
            Vector3 scale = DraftScale;
            if (!FormFirst) DestroyDraft();
            else Draft.gameObject.SetActive(false); // kept until the new model succeeds
            nextAsVersion = true;
            Hush();
            Ack(Crew.Maker, "On it! v" + next + " coming up.");
            if (shape.Count == 0)
            {
                // Sizes only: the same shape, built right away where the draft stood.
                var d = Design.Copy();
                if (clip) { d.width *= scale.x; d.rimHeight *= scale.y; d.grip *= scale.z; }
                else ApplyDraftSizes(d, FitShapes.Source(d.model));
                draftFrom = null;
                Design = d.Clamped();
                Note("design", Design.name, Design.Summary);
                StartCoroutine(FitAndBuild());
                return;
            }
            Shape = (Shape + ", " + change).Trim(' ', ',');
            StartCoroutine(Plan(change));
        }

        // The sizes changed on the draft, on a model design (its source mesh gives the proportions).
        private void ApplyDraftSizes(CoasterDesign d, Mesh source)
        {
            if (d == null || d.kind != "model" || draftSizesMm == Vector3.zero) return;
            float cut = d.cut;
            if (draftSizesMm.x > 0f) d.sizeW = draftSizesMm.x;
            if (draftSizesMm.z > 0f) d.sizeD = draftSizesMm.z;
            if (draftSizesMm.y > 0f) d.sizeH = draftSizesMm.y + cut;
            if (source != null)
            {
                var b = source.bounds;
                float scale = d.cupDiameter / Mathf.Max(1e-5f, Mathf.Max(b.size.x, b.size.z));
                float w = d.sizeW > 0f ? d.sizeW : b.size.x * scale, depth = d.sizeD > 0f ? d.sizeD : b.size.z * scale;
                d.modelHeight = (d.sizeH > 0f ? d.sizeH : b.size.y * scale) - cut;
                d.cupDiameter = Mathf.Max(w, depth);
                if (d.sizeW <= 0f && d.sizeD > 0f) d.sizeW = w;
                if (d.sizeD <= 0f && d.sizeW > 0f) d.sizeD = depth;
            }
            draftSizesMm = Vector3.zero;
        }

        private IEnumerator FlyToMaker(Flag f)
        {
            Flags.Remove(f);
            if (f.root == null) yield break;
            var root = f.root;
            root.SetParent(transform, true);
            Vector3 from = root.position;
            Vector3 to = cast.TryGetValue(Crew.Maker, out var m) && m != null ? m.transform.position + Vector3.up * .08f : from + Vector3.up * .1f;
            for (float t = 0f; t < 1f && root != null; t += Time.deltaTime / .5f)
            {
                root.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t)) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .06f;
                root.localScale = Vector3.one * Mathf.Lerp(1f, .3f, t);
                yield return null;
            }
            if (root != null) { ToyFx.Puff(root.position, .006f, 2); Destroy(root.gameObject); }
        }

        // ------------------------------------------------------------ frames

        private void DraftFrame()
        {
            if (Draft == null && Flags.Count == 0) return;
            if (Draft != null && draftMesh != null)
            {
                if (DraftIsClip) RefreshClipDraft();
                else draftMesh.localScale = DraftScale;
                if (draftTag != null)
                {
                    var b = draftMesh.GetComponent<MeshRenderer>().bounds;
                    Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - b.center, Vector3.up).normalized : Vector3.back;
                    draftTag.position = new Vector3(b.center.x, b.min.y + Mathf.Min(.016f, b.size.y * .3f), b.center.z) + toUser * (Mathf.Max(b.extents.x, b.extents.z) + .004f);
                    draftTag.rotation = SpeechBubbleDock.FacingAt(draftTag.position);
                }
            }
            PlaceFlags();
            DraftDimsFrame();
        }

        // A clip's draft is rebuilt from its sizes (a few times a second while they change).
        private void RefreshClipDraft()
        {
            draftMesh.localScale = Vector3.one;
            if (DraftScale == clipBuiltScale || Time.time < clipRebuildAt) return;
            clipRebuildAt = Time.time + .12f;
            clipBuiltScale = DraftScale;
            var d = draftDesign.Copy();
            d.width *= DraftScale.x;
            d.rimHeight *= DraftScale.y;
            d.grip *= DraftScale.z;
            var mesh = CoasterMesh.Build(d.Clamped());
            owned.Add(mesh);
            draftMesh.GetComponent<MeshFilter>().sharedMesh = mesh;
        }

#if UNITY_EDITOR
        // Editor mouse: drag a flag (click it, move, let go) and drag v2's size knobs.
        private bool EditorDraft(UnityEngine.InputSystem.Mouse mouse, Ray ray, Camera camera)
        {
            if (carryMouse && carried != null)
            {
                if (mouse.leftButton.isPressed)
                {
                    if (carryPlane.Raycast(ray, out float d)) MoveFlag(ray.GetPoint(d) + carryOffset);
                }
                else DropFlag();
                return true;
            }
            if (EditorKnob(mouse, ray, camera)) return true;
            if (!mouse.leftButton.wasPressedThisFrame) return false;
            var f = FlagNear(Vector3.positiveInfinity, ray);
            if (f == null) return false;
            Vector3 cloth = f.root.TransformPoint(new Vector3(.0125f, .042f, 0f));
            carryPlane = new Plane(-camera.transform.forward, cloth);
            PickFlag(f, null, f.root.position, null);
            carryOffset = f.root.position - cloth;
            carryMouse = true;
            return true;
        }
#endif
    }
}
