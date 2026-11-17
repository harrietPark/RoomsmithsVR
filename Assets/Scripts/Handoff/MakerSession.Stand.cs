using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    // F52 (2026-11-12, the user: "decide where the model goes before it's put down - not that tacky blue thing, something
    // like a dish or a shelf to set it on - and we can grab it and put it wherever we like"): the place for a new make is
    // a round wooden display stand (a foot, a waist and a wide plate with a raised lip; a fine groove on top marks the
    // model's footprint) instead of F50's see-through blue footprint.
    //  - When Maker asks where to put it, the stand pops up on a clear spot of the desk. Grab it near or far and put it
    //    anywhere: it stays upright, cannot go into the room (F38), and let go a little above a surface it settles onto
    //    it. "Place here" (pill or words) approves it.
    //  - Where Maker can work beside the stand, the wood block is carved right on it. Where it cannot (a shelf, the
    //    floor, a crowded spot), Maker works on the nearest clear desk spot and the finished thing hops onto the stand.
    //  - The stand stays under the made thing: moving the stand carries what stands on it; the thing can still be
    //    lifted off it. A thing on a stand is remembered with its stand and comes back on it.
    public sealed partial class MakerSession
    {
        public const float StandTop = .0145f, StandHeight = .017f, StandMargin = .012f;
        public static readonly Color StandWood = new Color32(234, 202, 156, 255), StandSide = new Color32(185, 136, 82, 255),
            StandGroove = new Color32(173, 121, 80, 255), StandFoot = new Color32(153, 108, 64, 255);

        public readonly List<Transform> Stands = new List<Transform>();
        public Transform PendingStand => pendingStand;
        public int StandHops { get; private set; }
        public bool MakerWorksOnStand { get; private set; }

        private readonly Dictionary<Transform, float> standRadius = new Dictionary<Transform, float>();
        private readonly List<Transform> roomStands = new List<Transform>();
        private readonly List<(Transform thing, Vector3 local, Quaternion turn)> riders = new List<(Transform, Vector3, Quaternion)>();
        private Transform pendingStand;
        private Material standMapleMaterial;
        private bool standStrict;

        public bool IsStand(Transform t) => t != null && standRadius.ContainsKey(t);
        public float StandRadiusOf(Transform stand) => stand != null && standRadius.TryGetValue(stand, out float r) ? r : 0f;
        public Vector3 StandTopOf(Transform stand) => stand.position + stand.up * StandTop;
        private static float StandRadiusFor(float modelRadius) => Mathf.Clamp(modelRadius * 1.08f + StandMargin, .035f, .26f);
        // 2026-11-13: props use the crew's shader (Toy Character), which turns sRGB vertex colours linear itself, so the
        // stand's colours stay as written (they used to be made linear here for Toy Surface).
        private static Color Lin(Color c) => c;

        // ------------------------------------------------------------ the stand itself

        private Transform MakeStand(Vector3 at, Quaternion turn, float modelRadius, float radius = -1f)
        {
            float R = radius > 0f ? radius : StandRadiusFor(modelRadius);
            float g = Mathf.Clamp(modelRadius + .004f, .012f, R - .011f);
            var profile = new List<(Vector2 p, Color c)>
            {
                (new Vector2(0f, 0f), StandFoot),
                (new Vector2(R * .58f, 0f), StandFoot),
                (new Vector2(R * .62f, .0025f), new Color32(170, 122, 71, 255)),
                (new Vector2(R * .55f, .006f), new Color32(171, 126, 82, 255)),
                (new Vector2(R * .4f, .0085f), new Color32(186, 141, 89, 255)),
                (new Vector2(R * .86f, .0112f), StandSide),
                (new Vector2(R, .0128f), new Color32(199, 148, 96, 255)),
                (new Vector2(R, .0158f), new Color32(232, 186, 128, 255)),
                (new Vector2(R - .004f, StandHeight), new Color32(241, 204, 153, 255)),
                (new Vector2(R - .0075f, StandTop), new Color32(229, 184, 125, 255)),
                (new Vector2(g + .0018f, StandTop), StandWood),
                (new Vector2(g, StandTop - .0007f), StandGroove),
                (new Vector2(g - .0018f, StandTop), StandWood),
                (new Vector2(0f, StandTop), new Color32(240, 209, 166, 255)),
            };
            var mesh = Lathe(profile, 48, "Display Stand");
            var vertices = mesh.vertices;
            var uv = new Vector2[vertices.Length];
            float textureSpan = R * (25f / 11f);
            float rimOffset = .52f * Mathf.Min(1f, R / .11f);
            // One grain direction across the face, with a slight height offset to carry it onto the rim.
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                uv[i] = new Vector2(.5f + v.x / textureSpan, .5f + (v.z + v.y * rimOffset) / textureSpan);
            }
            mesh.uv = uv;
            owned.Add(mesh);
            var root = new GameObject("Display Stand").transform;
            root.SetParent(transform, false);
            root.SetPositionAndRotation(at, Quaternion.Euler(0f, turn.eulerAngles.y, 0f));
            var look = Part("Stand", mesh);
            look.SetParent(root, false);
            var renderer = look.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = StandMapleMaterial();
            renderer.receiveShadows = false;
            // A simple solid to grab and to keep it out of the room while it is carried. F102: its top is the face a model
            // stands on (StandTop), not the rim's lip: a model on the stand used to start 2.5 mm inside it, so taking the
            // model pushed it out with a click and a carried stand pushed against its own model.
            var hull = Lathe(new List<(Vector2, Color)> { (new Vector2(0f, 0f), StandFoot), (new Vector2(R * .62f, 0f), StandFoot), (new Vector2(R, .0128f), StandSide), (new Vector2(R, StandTop), StandWood), (new Vector2(0f, StandTop), StandWood) }, 20, "Stand Hull");
            owned.Add(hull);
            var collider = look.gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = hull;
            collider.convex = true;
            var body = root.gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            standRadius[root] = R;
            Stands.Add(root);
            if (restoringAtLaunch) return root;
            root.localScale = Vector3.one * .05f;
            StartCoroutine(Grow(root, 1f, .28f));
            ToyFx.Puff(at + Vector3.up * .01f, .01f, 4);
            ToySounds.Play(ToySounds.Cue.Pop, at, .5f, 1.2f);
            return root;
        }

        private Material StandMapleMaterial()
        {
            if (standMapleMaterial != null) return standMapleMaterial;

            const int size = 256;
            var grain = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "Display Stand Maple Grain",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear
            };
            var pixels = new Color32[size * size];
            var random = new System.Random(38);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size;
                    float v = y / (float)size;
                    float bend = Mathf.PerlinNoise(u * 3f, v * 6f);
                    float grainShade = Mathf.PerlinNoise(u * 2.3f, v * 41f + 4f * bend);
                    float rings = .5f + .5f * Mathf.Sin((v * 47f + 2.2f * bend) * Mathf.PI * 2f);
                    float shade = .90f + .095f * grainShade + .041f * rings + .013f * (float)random.NextDouble();
                    pixels[y * size + x] = new Color(shade, shade, shade);
                }
            grain.SetPixels32(pixels);
            grain.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            owned.Add(grain);

            standMapleMaterial = new Material(material) { name = "Display Stand Maple" };
            standMapleMaterial.SetTexture("_BaseMap", grain);
            standMapleMaterial.SetColor("_BaseColor", Color.white);
            standMapleMaterial.SetFloat("_VertexColor", 1f);
            standMapleMaterial.SetFloat("_VertexSRGB", 1f);
            owned.Add(standMapleMaterial);
            return standMapleMaterial;
        }

        // A turned (lathed) shape from a profile (radius, height; colour), crisp between profile steps.
        private static Mesh Lathe(List<(Vector2 p, Color c)> profile, int segments, string name)
        {
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var col = new List<Color>();
            var tri = new List<int>();
            for (int k = 0; k + 1 < profile.Count; k++)
            {
                Vector2 a = profile[k].p, b = profile[k + 1].p;
                Vector2 d = b - a;
                if (d.sqrMagnitude < 1e-10f) continue;
                Vector2 out2 = new Vector2(d.y, -d.x).normalized;
                int start = v.Count;
                for (int s = 0; s <= segments; s++)
                {
                    float angle = s / (float)segments * Mathf.PI * 2f;
                    float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                    var normal = new Vector3(out2.x * cos, out2.y, out2.x * sin);
                    v.Add(new Vector3(a.x * cos, a.y, a.x * sin)); n.Add(normal); col.Add(Lin(profile[k].c));
                    v.Add(new Vector3(b.x * cos, b.y, b.x * sin)); n.Add(normal); col.Add(Lin(profile[k + 1].c));
                }
                for (int s = 0; s < segments; s++)
                {
                    int i0 = start + s * 2, i1 = i0 + 1, i2 = i0 + 2, i3 = i0 + 3;
                    Quad(i0, i1, i2, i3);
                }
                void Quad(int a0, int b0, int a1, int b1)
                {
                    Vector3 facing = n[a0] + n[a1];
                    Tri(a0, b0, a1, facing);
                    Tri(a1, b0, b1, facing);
                }
                void Tri(int x, int y, int z, Vector3 facing)
                {
                    Vector3 cross = Vector3.Cross(v[y] - v[x], v[z] - v[x]);
                    if (cross.sqrMagnitude < 1e-14f) return;
                    if (Vector3.Dot(cross, facing) < 0f) tri.AddRange(new[] { x, z, y });
                    else tri.AddRange(new[] { x, y, z });
                }
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetColors(col);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private void DestroyStand(Transform stand)
        {
            if (stand == null) return;
            if (held == stand) { holdHand = null; mouseHolding = false; held = null; heldBody = null; }
            if (projectPlate == stand) projectPlate = null;
            Stands.Remove(stand);
            roomStands.Remove(stand);
            standRadius.Remove(stand);
            if (pendingStand == stand) pendingStand = null;
            if (workStand == stand) { workStand = null; workCarried = false; } // F89
            ToyFx.Puff(stand.position + Vector3.up * .01f, .008f, 3);
            Destroy(stand.gameObject);
        }

        // ------------------------------------------------------------ what stands on a stand

        private bool StandsOn(Transform thing, Transform stand)
        {
            if (thing == null || stand == null || thing == stand || IsStand(thing)) return false;
            var r = thing.GetComponentInChildren<MeshRenderer>();
            if (r == null) return false;
            var b = r.bounds;
            Vector3 top = StandTopOf(stand);
            if (Mathf.Abs(b.min.y - top.y) > .02f) return false;
            return new Vector2(b.center.x - top.x, b.center.z - top.z).magnitude <= StandRadiusOf(stand);
        }

        // The stand something stands on (null if none).
        private Transform StandUnder(Transform thing)
        {
            foreach (var s in Stands) if (s != null && StandsOn(thing, s)) return s;
            return null;
        }

        private float StandRadiusUnder(Transform thing) => StandRadiusOf(StandUnder(thing));

        private bool Empty(Transform stand)
        {
            foreach (var t in MadeThings()) if (StandsOn(t, stand)) return false;
            return true;
        }

        // Grabbed: whatever stands on it goes along.
        private void PickRiders(Transform stand)
        {
            riders.Clear();
            foreach (var t in MadeThings())
                if (StandsOn(t, stand))
                    riders.Add((t, Quaternion.Inverse(stand.rotation) * (t.position - stand.position), Quaternion.Inverse(stand.rotation) * t.rotation));
            PickWork(stand); // F89: Maker's work on it (and Maker) too
        }

        private void CarryRiders(Transform stand)
        {
            foreach (var (thing, local, turn) in riders)
            {
                if (thing == null) continue;
                thing.SetPositionAndRotation(stand.position + stand.rotation * local, stand.rotation * turn);
                var body = thing.GetComponent<Rigidbody>();
                if (body != null) { body.position = thing.position; body.rotation = thing.rotation; }
            }
            CarryWork(stand); // F89
        }

        // A stand is always upright (F97: and keeps its turn while carried).
        private static Quaternion Upright(Quaternion rot) => Quaternion.Euler(0f, rot.eulerAngles.y, 0f);

        // F97 (Quest 2026-11-15, the user: "grabbing and moving the plate with v1, v2, v3 is too hard, make it easier"):
        // the plate is 1.7 cm high, so a pinch had to land within 4 cm of its bounds and nearer the plate's middle than
        // the model's, and a pinch at the rim mostly lifted the model off it. Now a pinch anywhere in a ring round the
        // plate takes the stand: out to StandGripOut past the rim, from StandGripBelow under it to StandGripAbove over
        // its top; over the plate's middle, where the model stands, only below the model's own footprint edge.
        public const float StandGripOut = .05f, StandGripBelow = .03f, StandGripAbove = .035f;
        public Transform StandGripAt(Vector3 point)
        {
            if (float.IsInfinity(point.x) || Stands.Count == 0) return null;
            // (The < > arrows on the stand's front rim keep their own pinch.)
            if (PlatePagerBackShown && OnStandArrow(pagerBackButton, point, .012f, -.03f, .03f)) return null;
            if (PlatePagerNextShown && OnStandArrow(pagerNextButton, point, .012f, -.03f, .03f)) return null;
            Transform best = null;
            float bestDistance = float.MaxValue;
            var things = MadeThings();
            foreach (var stand in Stands)
            {
                if (stand == null || !stand.gameObject.activeInHierarchy || !things.Contains(stand)) continue;
                float R = StandRadiusOf(stand);
                Vector3 top = StandTopOf(stand);
                float across = new Vector2(point.x - top.x, point.z - top.z).magnitude;
                float up = point.y - top.y;
                if (across > R + StandGripOut || up < -(StandTop + StandGripBelow) || up > StandGripAbove) continue;
                // F102: the model on it takes the pinch when the fingers are nearer the model than the plate.
                if (RiderNearer(stand, point, things, out _)) continue;
                float d = Mathf.Abs(across - R) + Mathf.Max(0f, up);
                if (d < bestDistance) { bestDistance = d; best = stand; }
            }
            return best;
        }

        // F102 (Quest 2026-11-16, the user: "lifting the stand should lift the model too; lifting the model, only the
        // model"): a pinch by a stand goes to whichever is nearer the fingers: the model standing on it (its bounds, with
        // RiderBonus) or the plate (its disc). The stand taken carries its model (PickRiders); the model taken goes alone.
        public const float RiderBonus = .006f;

        // The model on a stand nearer the pinch than the stand's plate (MadeNear takes it before anything else).
        private Transform StandRiderAt(Vector3 point)
        {
            if (float.IsInfinity(point.x) || Stands.Count == 0) return null;
            var things = MadeThings();
            foreach (var stand in Stands)
            {
                if (stand == null || !stand.gameObject.activeInHierarchy || !things.Contains(stand)) continue;
                if (PlateDistance(stand, point) > StandGripOut + .01f) continue;
                if (RiderNearer(stand, point, things, out var rider)) return rider;
            }
            return null;
        }

        private bool RiderNearer(Transform stand, Vector3 point, List<Transform> things, out Transform rider)
        {
            rider = null;
            float plate = PlateDistance(stand, point), best = float.MaxValue;
            foreach (var t in things)
            {
                if (t == stand || IsStand(t) || t == sizeBox || !StandsOn(t, stand)) continue;
                var r = t.GetComponentInChildren<MeshRenderer>();
                if (r == null) continue;
                float d = Mathf.Sqrt(r.bounds.SqrDistance(point));
                if (d - RiderBonus < plate && d < best) { best = d; rider = t; }
            }
            return rider != null;
        }

        // From a point to the stand's plate (a disc StandTop thick, StandRadiusOf wide).
        private float PlateDistance(Transform stand, Vector3 point)
        {
            Vector3 top = StandTopOf(stand);
            float across = new Vector2(point.x - top.x, point.z - top.z).magnitude;
            float outside = Mathf.Max(0f, across - StandRadiusOf(stand));
            float vertical = Mathf.Max(0f, Mathf.Max(point.y - top.y, top.y - StandTop - point.y));
            return Mathf.Sqrt(outside * outside + vertical * vertical);
        }

        // F98 (Quest 2026-11-15, the user: "the bowl is still really weird to grab - it keeps going down under my desk"): a
        // stand followed the pinch point turned with the wrist (it swung round the fingers), and with grab & release it
        // followed the open hand down past the desk's front edge, sliding down the desk's side (pushed out of the desk)
        // and staying there when let go. Now it follows the hand's position only, never lower than the top of the
        // furniture under it (or, past the furniture, than where it was taken from), and let go with no furniture under
        // it, it glides onto the nearest clear desk spot.
        public int StandLandings { get; private set; }

        // The lowest a carried stand may go at `at`: the top of the furniture (not the floor) under it, else the height
        // it was taken from.
        private float StandLowest(Vector3 at)
        {
            float top = FurnitureTopUnder(at, Mathf.Max(at.y, standGrabY) + .02f, out bool found);
            return found ? top : standGrabY;
        }

        // The highest furniture top (not the floor) whose outline holds `at`, no higher than `below`.
        private float FurnitureTopUnder(Vector3 at, float below, out bool found)
        {
            found = false;
            float best = float.NegativeInfinity;
            var room = arrival != null ? arrival.handoff.meeting.environment : null;
            if (room == null) return best;
            var xz = new Vector2(at.x, at.z);
            foreach (var surface in room.Surfaces)
            {
                if (surface.Polygon == null || surface.IsFloor || !surface.Contains(xz, -.01f)) continue;
                float y = surface.Point(xz).y;
                if (y <= below && y > best) { best = y; found = true; }
            }
            return best;
        }

        // Let go: a little above a surface it settles onto it (with what it carries); over furniture higher up it comes
        // down onto it; past the furniture it glides onto the nearest clear desk spot (F98).
        private void StandLetGo(Transform stand)
        {
            Heard();
            Talk.Add("Person moved the stand");
            var carried = new List<Transform>();
            foreach (var r in riders) if (r.thing != null) carried.Add(r.thing);
            var room = arrival.handoff.meeting.environment;
            if (room != null && Physics.Raycast(stand.position + Vector3.up * .01f, Vector3.down, out var hit, .1f, room.GeometryMask, QueryTriggerInteraction.Ignore) &&
                hit.normal.y > .8f && hit.distance > .012f)
                StartCoroutine(SettleStand(stand, hit.point, carried));
            else if (room != null && Physics.Raycast(stand.position + Vector3.up * .01f, Vector3.down, out hit, .1f, room.GeometryMask, QueryTriggerInteraction.Ignore) && hit.normal.y > .8f)
            {
                // Already resting on a surface.
                ToySounds.Play(ToySounds.Cue.Press, stand.position, .3f, 1.45f);
                foreach (var t in carried) StartCoroutine(RememberWhenSettled(t));
                LandWork(stand); // F89: Maker is set down beside its work
            }
            else
            {
                float top = FurnitureTopUnder(stand.position, stand.position.y + .02f, out bool over);
                Vector3 onto = over ? new Vector3(stand.position.x, top, stand.position.z) : FindPlacement(stand.position, StandRadiusOf(stand));
                bool onDesk = over || DeskTop(onto, out _);
                StandLandings++;
                WriteInputLog("stand let go " + (over ? "over furniture: settles onto it" : onDesk ? "past the furniture: glides onto the nearest clear desk spot" : "with no desk near: stays"), true);
                if (onDesk) StartCoroutine(SettleStand(stand, onto, carried));
                else
                {
                    ToySounds.Play(ToySounds.Cue.Press, stand.position, .3f, 1.45f);
                    foreach (var t in carried) StartCoroutine(RememberWhenSettled(t));
                    LandWork(stand);
                }
            }
        }

        private IEnumerator SettleStand(Transform stand, Vector3 onto, List<Transform> carried)
        {
            Vector3 from = stand.position;
            float time = Mathf.Clamp(Vector3.Distance(from, onto) * 1.2f, .14f, .5f);
            for (float t = 0f; t < 1f && stand != null && held != stand; t += Time.deltaTime / time)
            {
                stand.position = Vector3.Lerp(from, onto, t * t);
                CarryRiders(stand);
                yield return null;
            }
            if (stand == null || held == stand) yield break;
            stand.position = onto;
            CarryRiders(stand);
            ToySounds.Play(ToySounds.Cue.Press, onto, .45f, 1.3f);
            ToyFx.Puff(onto + Vector3.up * .004f, .006f, 2);
            foreach (var t in carried) StartCoroutine(RememberWhenSettled(t));
            LandWork(stand); // F89: Maker is set down beside its work
        }

        // ------------------------------------------------------------ choosing the place (F50's placement step)

        // The stand the next make goes onto: shown and grabbable until "Place here". `strict`: the work happens on
        // the stand itself (a repaint moves the made thing there), so it must stand where Maker can work.
        private Transform ShowStand(Vector3 near, Quaternion turn, float modelRadius, bool strict)
        {
            standStrict = strict;
            if (projectPlate != null) return projectPlate;
            float R = StandRadiusFor(modelRadius);
            // An earlier stand that was approved but never got its make is used again when it is the right size
            // (no pile of empty stands); a wrong-sized one goes.
            if (pendingStand != null && Empty(pendingStand))
            {
                var earlier = pendingStand;
                pendingStand = null;
                if (Mathf.Abs(StandRadiusOf(earlier) - R) < .01f) return earlier;
                DestroyStand(earlier);
            }
            // The spot is found as F50 found its footprint: by the model's own size (the plate's rim may reach a little further).
            return MakeStand(FindPlacement(near, modelRadius), turn, modelRadius);
        }

        // "Place here": right on the stand when Maker can work beside it; else (not strict) on the nearest clear desk
        // spot, and the made thing hops onto the stand when it is done.
        private bool ApproveStand(Transform stand)
        {
            float R = StandRadiusOf(stand);
            Vector3 top = StandTopOf(stand);
            if (stand == projectPlate)
            {
                approvedPlace = top;
                approvedTurn = Upright(stand.rotation);
                MakerWorksOnStand = false;
                pendingStand = stand;
                return true;
            }
            approvedTurn = Upright(stand.rotation);
            if (PlacementClear(stand.position, placementRadius))
            {
                approvedPlace = top;
                MakerWorksOnStand = true;
                pendingStand = stand;
                return true;
            }
            if (!standStrict && WorkSpotNear(stand.position, R, out var spot))
            {
                approvedPlace = spot;
                MakerWorksOnStand = false;
                pendingStand = stand;
                Say(Crew.Maker, "I'll make it over here, then put it on your stand.");
                return true;
            }
            Caption(Crew.Maker, standStrict ? "I need clear desk around the stand to paint. Move it a little?" : "I need a bit of clear desk to work. Make some room?", 8f);
            return false;
        }

        // The nearest clear desk spot to work on, away from the stand itself.
        private bool WorkSpotNear(Vector3 from, float standR, out Vector3 spot)
        {
            spot = from;
            Vector3 start = from;
            if (Head != null && !DeskTop(from, out _)) start = DeskPointInFront();
            for (float r = .1f; r <= 1.2f; r += .08f)
                for (int i = 0; i < 16; i++)
                {
                    Vector3 p = start + Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward * r;
                    if (!DeskTop(p, out float y)) continue;
                    var at = new Vector3(p.x, y, p.z);
                    if (Vector3.ProjectOnPlane(at - from, Vector3.up).magnitude < standR + placementRadius + .03f) continue;
                    if (!PlacementClear(at, placementRadius)) continue;
                    spot = at;
                    return true;
                }
            return false;
        }

        // After a make (MakerSession.Build): the made thing goes onto its stand (a hop, if it was made elsewhere or
        // the stand was moved meanwhile).
        private IEnumerator SettleOnStand()
        {
            var stand = pendingStand ?? projectPlate;
            pendingStand = null;
            if (stand == null || Product == null) yield break;
            projectPlate = stand;
            Vector3 top = StandTopOf(stand);
            Vector3 from = Product.position;
            Quaternion turn = Product.rotation;
            if (Vector3.Distance(from, top) < .008f) { Product.position = top; SyncBody(Product); yield break; }
            StandHops++;
            float span = Vector3.ProjectOnPlane(top - from, Vector3.up).magnitude;
            float apex = Mathf.Max(from.y, top.y) + .06f + span * .25f;
            float seconds = Mathf.Clamp(.45f + span * .35f, .45f, .9f);
            ToySounds.Play(ToySounds.Cue.Whoosh, from, .4f, 1.4f);
            Cue(Crew.Maker, "encourage");
            for (float t = 0f; t < 1f && Product != null && held != Product; t += Time.deltaTime / seconds)
            {
                Vector3 p = ArcPoint(from, top, apex, t);
                Product.SetPositionAndRotation(p, turn * Quaternion.Euler(0f, 360f * Mathf.SmoothStep(0f, 1f, t), 0f));
                SyncBody(Product);
                yield return null;
            }
            if (Product == null || held == Product) yield break;
            Product.SetPositionAndRotation(top, turn);
            SyncBody(Product);
            ToySounds.Play(ToySounds.Cue.Press, top, .5f, 1.25f);
            ToyFx.Puff(top + Vector3.up * .005f, .008f, 3);
        }

        // A ballistic arc from a to b through `apex` (eased at the ends), like the title's hops.
        private static Vector3 ArcPoint(Vector3 a, Vector3 b, float apex, float t)
        {
            t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            float up = Mathf.Sqrt(Mathf.Max(.0001f, apex - a.y)), down = Mathf.Sqrt(Mathf.Max(.0001f, apex - b.y));
            float peak = up / (up + down);
            Vector3 p = Vector3.Lerp(a, b, t);
            float u = t < peak ? (peak - t) / peak : (t - peak) / (1f - peak);
            p.y = apex - (t < peak ? apex - a.y : apex - b.y) * u * u;
            return p;
        }

        private static void SyncBody(Transform thing)
        {
            var body = thing != null ? thing.GetComponent<Rigidbody>() : null;
            if (body != null) { body.position = thing.position; body.rotation = thing.rotation; }
        }

        // Restoring the room: a thing remembered on a stand comes back on one. F104 (Quest 2026-11-16, the user: "when the
        // stand is lifted the thing in it should come too"): the stand came back under the thing's pivot, turned with the
        // thing, so a thing that had been put on it tilted (the heart, 34 degrees) stood 3 cm off its top: it was no
        // longer "on" the stand, and lifting the stand left it behind (and the next save forgot the stand). The stand now
        // comes back exactly where it was relative to the thing (standAt/standTurn); older saves put it under the
        // thing's lowest point, centred under it.
        public int StandsRestored { get; private set; }
        private void RestoreStand(Transform made, float radius, float[] at = null, float[] turn = null)
        {
            if (RestoreWithoutStand(made, radius, turn)) return; // F105: nothing comes back on a stand
            if (made == null || radius <= 0f) return;
            var r = made.GetComponentInChildren<MeshRenderer>();
            float modelRadius = r != null ? Mathf.Max(r.bounds.extents.x, r.bounds.extents.z) : radius * .7f;
            Vector3 position = made.position - Vector3.up * StandTop;
            Quaternion rotation = made.rotation;
            if (at != null && at.Length == 3 && turn != null && turn.Length == 4)
            {
                position = made.position + made.rotation * new Vector3(at[0], at[1], at[2]);
                rotation = made.rotation * new Quaternion(turn[0], turn[1], turn[2], turn[3]);
            }
            else if (r != null)
            {
                var b = r.bounds;
                position = new Vector3(b.center.x, b.min.y - StandTop, b.center.z);
            }
            var stand = MakeStand(position, rotation, modelRadius, radius);
            roomStands.Add(stand);
            StandsRestored++;
        }

        // F104: what is saved about the stand a thing stands on: its radius (F52) and its pose relative to the thing.
        private void SaveStandPose(Made item, Transform thing)
        {
            if (item == null) return;
            var stand = thing != null ? StandUnder(thing) : null;
            item.stand = StandRadiusOf(stand);
            if (stand == null) { item.standAt = item.standTurn = null; return; }
            Quaternion back = Quaternion.Inverse(thing.rotation);
            Vector3 at = back * (stand.position - thing.position);
            Quaternion turn = back * stand.rotation;
            item.standAt = new[] { at.x, at.y, at.z };
            item.standTurn = new[] { turn.x, turn.y, turn.z, turn.w };
        }

        // F104: a made thing let go just above a stand (its middle over the plate, its lowest point at most
        // DropOntoStandAbove over the top) comes down onto it, so it stands on it and goes along when the stand is lifted.
        public const float DropOntoStandAbove = .06f;
        public int DropsOntoStand { get; private set; }
        private bool DropOntoStand(Transform thing)
        {
            if (thing == null || IsStand(thing) || !MadeThings().Contains(thing)) return false;
            var r = thing.GetComponentInChildren<MeshRenderer>();
            if (r == null) return false;
            foreach (var stand in Stands)
            {
                if (stand == null || !stand.gameObject.activeInHierarchy || StandsOn(thing, stand)) continue;
                var b = r.bounds;
                Vector3 top = StandTopOf(stand);
                float above = b.min.y - top.y;
                if (above < -.01f || above > DropOntoStandAbove) continue;
                if (new Vector2(b.center.x - top.x, b.center.z - top.z).magnitude > StandRadiusOf(stand) * .85f) continue;
                DropsOntoStand++;
                WriteInputLog("made thing let go " + Mathf.RoundToInt(above * 100f) + " cm over a stand: comes down onto it", true);
                StartCoroutine(SettleOntoStand(thing, above));
                return true;
            }
            return false;
        }

        private IEnumerator SettleOntoStand(Transform thing, float drop)
        {
            Vector3 from = thing.position, to = from - Vector3.up * drop;
            float time = Mathf.Clamp(Mathf.Abs(drop) * 4f, .08f, .25f);
            for (float t = 0f; t < 1f && thing != null && held != thing; t += Time.deltaTime / time)
            {
                thing.position = Vector3.Lerp(from, to, t * t);
                SyncBody(thing);
                yield return null;
            }
            if (thing == null || held == thing) yield break;
            thing.position = to;
            SyncBody(thing);
            ToySounds.Play(ToySounds.Cue.Press, to, .4f, 1.3f);
            StartCoroutine(RememberWhenSettled(thing));
        }

        private void ClearRoomStands()
        {
            foreach (var s in new List<Transform>(roomStands)) DestroyStand(s);
            roomStands.Clear();
        }
    }
}
