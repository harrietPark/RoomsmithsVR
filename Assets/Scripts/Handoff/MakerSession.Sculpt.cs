using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Pet = RoomCapsule.CapsulePetController;

namespace Windups.Handoff
{
    // F33 (2026-11-11): while Meshy makes the 3D model, Maker makes it in front of the person, following the
    // real stages (the work is shown by the character acting, never by anything inside a card).
    // F35 (2026-11-12, chosen designs "carving" and "big brush") replaced the clay and the dotting brush:
    //  1. Block (stage Clay): a wood block about the made thing's width pops onto the spot, a face turned to
    //     Maker; Maker walks up beside it, holds a chisel along that face with its left mitten and taps the
    //     chisel's end with a mallet in its right; every tap ("tok") cuts the block's vertical corners deeper
    //     (fresh light wood shows) and a few chips fly. All of it stays low, clear of the big head.
    //  2. Shape (Meshy's untextured mesh, ~4 s): after the tap in progress the tools go, the block grows just
    //     around the real shape, big chunks fall off ("tuk tuk") and the shape stands there in wood; Maker
    //     blows the dust away ("hoo"), Fixer nods.
    //  3. Paint (Meshy's retexture, ~55 s): Maker takes a big brush (wood handle, metal ferrule, a bristle tuft
    //     wet with paint), dips it in a paint pot beside it ("plop", the pot in the picture's colour) and paints
    //     wide bands across the model's real surface on its own side, within reach ("swish"), one band per dip,
    //     now and then a drip; after a few bands it walks round to the next side (the pot comes along).
    //  4. Reveal: after the stroke in progress, Work_Done, a burst of paint, and the model wears its texture;
    //     Maker nods, Spark cheers. Fixer then checks it (MakerSession.FitAndBuild) and the made thing takes
    //     this view's place (Build with revealedBySculpt).
    // Maker never reaches more than HandReach from its body, leans at most SculptLean and looks at most
    // SculptHeadDown below the horizon, and stands where its big head stays out of the work (HeadDepth).
    // Held, or put down somewhere else, it stops working and walks back to its place. Failures: no shape ->
    // the block crumbles away; no colours -> the shape keeps the bands painted so far (the person can paint it).
    public sealed partial class MakerSession
    {
        public enum SculptStage { None, Approach, Clay, Shape, Paint, Reveal }
        public SculptStage Sculpt { get; private set; }
        public int Pats { get; private set; }               // mallet taps on the block (F35; were pats on clay)
        public int Dabs { get; private set; }               // brush touches painted (F35: each step of a band)
        public int Bands { get; private set; }              // painted bands
        public int Dips { get; private set; }               // times the brush went into the pot
        public int Chunks { get; private set; }             // pieces that fell off the block when the shape came
        public int SculptSteps { get; private set; }        // Maker stepped back to keep its head clear
        public float Chamfer => chamfer;                    // how deep the block's corners are cut (part of its size)
        public Transform SculptView => view;
        public Transform ClayLump => block;                 // the wood block (F35; the name stays for the checks)
        public Transform MakerBrush => makerBrush;
        public Transform PaintPot => pot;
        public Transform Chisel => chisel;
        public Transform Mallet => mallet;
        public Vector3 SculptSpot => sculptSpot;
        public const float SculptLean = 5f, SculptHeadDown = 16f, BrushLength = .06f;
        public static float PatGap = .1f;

        private Transform block, view, makerBrush, sculptFocus, chisel, mallet, pot, potPaint;
        private readonly List<Transform> dots = new List<Transform>(); // painted bands and drips (on the view)
        private readonly List<Transform> chips = new List<Transform>();
        private float chamfer;
        private Quaternion blockTurn = Quaternion.identity;
        private Vector3 sculptSpot, standPoint, makerHome;
        private Quaternion sculptTurn;
        private float blockHalf, blockHeight;
        private Mesh blockMesh, viewMesh;
        private Color[] paintColours = { new Color(.9f, .6f, .35f) };
        private Transform hiddenProduct;
        private bool handBusy, sculptReachable;
        private float nextStandCheck;

        // A warm mid wood (the toy shading lightens it): fresh cuts show light against it, and both light and
        // dark paint stand out on it.
        private static readonly Color WoodColour = new Color(.74f, .53f, .32f), WoodGrain = new Color(.67f, .47f, .28f),
            WoodFresh = new Color(.95f, .83f, .62f), WoodEnd = new Color(.84f, .66f, .45f), WoodDark = new Color(.5f, .34f, .2f);

        // ------------------------------------------------------------ 1. block

        private IEnumerator BeginSculpt(Vector3 spot, Quaternion turn, float widthMm)
        {
            Sculpt = SculptStage.Approach;
            Pats = Dabs = Chunks = 0;
            WorkAngles.Clear();
            sculptSpot = spot;
            sculptTurn = turn;
            // (F40: v2 made beside v1 from the draft leaves v1 in view.)
            if (versionSpot == null && Product != null && Product.gameObject.activeSelf) { hiddenProduct = Product; Product.gameObject.SetActive(false); }
            else if (versionSpot != null && Product != null && productFinished) ClearPlateForVersion(Product); // F81: the plate is emptied first
            if (sculptFocus == null) { sculptFocus = new GameObject("Sculpt Focus").transform; sculptFocus.SetParent(transform, false); }
            sculptFocus.position = spot + Vector3.up * .02f;
            // A wood block about the made thing's width, one face turned to Maker (so it works that face).
            float width = Mathf.Clamp(widthMm * .001f * .8f, .05f, .1f);
            blockHalf = width * .5f;
            blockHeight = Mathf.Clamp(width * .6f, .035f, .046f); // its top below Maker's chin: the big head stays clear
            chamfer = ChamferStart;
            if (blockMesh == null) { blockMesh = new Mesh { name = "WoodBlock" }; blockMesh.MarkDynamic(); owned.Add(blockMesh); }
            WoodBlock(blockMesh, chamfer);
            if (block == null)
            {
                block = Part("Wood Block", blockMesh);
                block.SetParent(transform, false);
            }
            cast.TryGetValue(Crew.Maker, out var worker);
            Vector3 toWorker = worker != null ? Vector3.ProjectOnPlane(worker.transform.position - spot, Vector3.up) : Vector3.zero;
            blockTurn = toWorker.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(toWorker.normalized, Vector3.up) : turn;
            block.gameObject.SetActive(true);
            block.SetPositionAndRotation(spot, blockTurn);
            block.localScale = Vector3.zero;
            StartCoroutine(ScaleTo(block, BlockScale(1f, 1f), .28f));
            ToyFx.Puff(spot + Vector3.up * .01f, .01f, 4);
            ToySounds.Play(ToySounds.Cue.Bonk, spot, .4f, 1.4f);
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null) yield break;
            var pet = maker.movement;
            makerHome = pet.transform.position;
            Ack(Crew.Maker, "Carving it!");
            yield return WalkToWork(maker, blockHalf + .01f);
            if (!MakerAtWork(maker)) { Say(Crew.Maker, "I need a clear place to stand beside this model."); yield break; }
            var body = WindupBody.Of(maker);
            if (body != null) { body.StartWork("Work_Loop"); body.LimitPosture(SculptLean, SculptHeadDown); }
            else Cue(Crew.Maker, "thinking");
            if (Sculpt == SculptStage.Approach) Sculpt = SculptStage.Clay;
            StartCoroutine(CarveBlock(maker));
        }

        private Vector3 BlockScale(float spread, float tall) => new Vector3(blockHalf * 2f * spread, blockHeight * tall, blockHalf * 2f * spread);

        // Beside the spot, on Maker's side, close enough for the mittens (and the head clear of the work).
        private IEnumerator WalkToWork(WindupCharacter maker, float radius)
        {
            var pet = maker.movement;
            var room = arrival.handoff.meeting.environment;
            Vector3 from = Vector3.ProjectOnPlane(pet.transform.position - sculptSpot, Vector3.up);
            Vector3 dir = from.sqrMagnitude > 1e-4f ? from.normalized : -Vector3.ProjectOnPlane(Head != null ? Head.forward : Vector3.forward, Vector3.up).normalized;
            sculptReachable = false;
            // Close enough for the mittens, far enough that the big head stays out of the block. Straight from
            // where Maker is first, else turning round the block (F35: near a desk edge the straight side may
            // have no room); the block turns a face to the side chosen.
            // F35: first the two sides WorkSideAngle off the person's line (Maker never works the far side, where the
            // person cannot see the work), the one nearer Maker first; then round from where it is.
            var sides = new List<Vector3>();
            if (Head != null)
            {
                Vector3 toPerson = Vector3.ProjectOnPlane(Head.position - sculptSpot, Vector3.up).normalized;
                foreach (float angle in new[] { WorkSideAngle, 90f })
                {
                    Vector3 a = Quaternion.Euler(0f, angle, 0f) * toPerson, b = Quaternion.Euler(0f, -angle, 0f) * toPerson;
                    if (Vector3.Dot(b, dir) > Vector3.Dot(a, dir)) { var swap = a; a = b; b = swap; }
                    sides.Add(a);
                    sides.Add(b);
                }
            }
            foreach (float turnBy in new[] { 0f, 30f, -30f, 60f, -60f, 95f, -95f, 140f, -140f, 180f }) sides.Add(Quaternion.Euler(0f, turnBy, 0f) * dir);
            // (2026-11-12) First a side where nobody stands within Maker's work room; else the old 7 cm (the others
            // then step out of the work space, WorkSpaceFrame).
            foreach (float keep in new[] { MakerWorkRoom + CrewBodyRadius })
            foreach (var d in sides)
            {
                if (sculptReachable) break;
                var faceTurn = Quaternion.LookRotation(d, Vector3.up);
                float near = Mathf.Max(radius + .045f, ClearStand(maker, blockMesh, sculptSpot, faceTurn, BlockScale(1f, 1f), d, radius + .045f));
                foreach (float extra in new[] { 0f, .01f, .02f })
                {
                    Vector3 want = sculptSpot + d * (near + extra);
                    if (!room.TrySameLevelNavPoint(want, out var nav) || Mathf.Abs(nav.y - sculptSpot.y) > .04f) continue;
                    // The walkable point the room gives must be where it was asked for: snapped toward the work
                    // (near a desk edge), the big head would be in the block.
                    if (Vector3.ProjectOnPlane(nav - want, Vector3.up).magnitude > .012f || Vector3.ProjectOnPlane(nav - sculptSpot, Vector3.up).magnitude < near - .004f) continue;
                    if (cast.Values.Any(c => c != null && c != maker && Vector3.ProjectOnPlane(c.transform.position - nav, Vector3.up).magnitude < keep)) continue;
                    if (!MakerStandClear(nav)) continue;
                    if (!Walkable(pet.transform.position, nav, 2.5f)) continue; // a way there that Maker can walk
                    standPoint = nav;
                    sculptReachable = true;
                    break;
                }
                if (!sculptReachable) continue;
                NoteWorkAngle();
                if (block != null && block.gameObject.activeSelf && Quaternion.Angle(blockTurn, faceTurn) > 1f) { blockTurn = faceTurn; StartCoroutine(TurnTo(block, faceTurn, .25f)); }
                break;
            }
            if (!sculptReachable) yield break; // no safe work position: keep the tools hidden
            if (Vector3.Distance(pet.transform.position, standPoint) > .02f)
            {
                pet.UnlockMeetingSeat();
                yield return null;
                if (pet.GoTo(standPoint, 2.5f, true))
                    for (float until = Time.time + 8f; Time.time < until && pet.State != Pet.MotionState.Idle && !pet.IsHeld; ) yield return null;
            }
            for (int i = 0; i < 30 && !pet.MeetingSeatLocked && !pet.IsHeld; i++) { pet.LockMeetingSeat(); yield return null; }
            yield return new WaitForSeconds(.25f); // turned to the work
        }

        public const float WorkSideAngle = 60f;
        // Where Maker works from, seen from above: degrees between the person's line through the work and Maker
        // (0 = between the work and the person, 180 = the far side, out of the person's sight).
        public float WorkAngle { get; private set; } = -1f;
        public readonly List<float> WorkAngles = new List<float>();
        private void NoteWorkAngle()
        {
            if (Head == null) return;
            WorkAngle = Vector3.Angle(Vector3.ProjectOnPlane(Head.position - sculptSpot, Vector3.up), Vector3.ProjectOnPlane(standPoint - sculptSpot, Vector3.up));
            WorkAngles.Add(WorkAngle);
        }

        // From where Maker works now to the same angle on the other side of the person's line (degrees, in
        // MoveToPaintSide's turn), so the painting goes on where the person can see it.
        private float TurnToMirrorSide(WindupCharacter maker)
        {
            if (Head == null || maker == null) return 70f;
            Vector3 toPerson = Vector3.ProjectOnPlane(Head.position - sculptSpot, Vector3.up).normalized;
            Vector3 now = Vector3.ProjectOnPlane(maker.transform.position - sculptSpot, Vector3.up).normalized;
            float side = Vector3.Dot(Vector3.Cross(toPerson, now), Vector3.up) >= 0f ? 1f : -1f;
            Vector3 target = Quaternion.Euler(0f, -side * WorkSideAngle, 0f) * toPerson;
            float Deg(Vector3 v) => Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg;
            return Mathf.DeltaAngle(Deg(now), Deg(target));
        }

        // A whole path on the room's walkable surfaces from `from` to `to`, no longer than `most`.
        private bool Walkable(Vector3 from, Vector3 to, float most)
        {
            if (Vector3.Distance(from, to) < .03f) return true;
            var room = arrival.handoff.meeting.environment;
            var path = new UnityEngine.AI.NavMeshPath();
            if (!UnityEngine.AI.NavMesh.CalculatePath(from, to, room.Query, path) || path.status != UnityEngine.AI.NavMeshPathStatus.PathComplete) return false;
            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            return length <= most;
        }

        // (F89: carried along with its work on the stand, it is still at work.)
        private bool MakerAtWork(WindupCharacter maker) =>
            (workCarried && carryMaker && maker != null && maker.movement == carriedPet && maker.movement.MeetingSeatLocked) ||
            sculptReachable && maker != null && !maker.movement.IsHeld && MakerStandClear(maker.transform.position) &&
            Vector3.ProjectOnPlane(maker.transform.position - standPoint, Vector3.up).magnitude < .04f && Mathf.Abs(maker.transform.position.y - standPoint.y) < .04f;

        // ------------------------------------------------------------ carving (F35)
        // The left mitten holds a chisel along the face turned to Maker, its edge at the face's left corner and
        // its handle to Maker's right; the right mitten swings a mallet onto the handle's end. Everything stays
        // low (below the head) and within HandReach. Each tap cuts the vertical corners a little deeper.
        public const float ChamferStart = .04f, ChamferMax = .3f, ChamferPerTap = .022f, ChiselLength = .034f, MalletReach = .03f;

        private IEnumerator CarveBlock(WindupCharacter maker)
        {
            var body = WindupBody.Of(maker);
            BuildCarvingTools();
            int steps = 0;
            while (Sculpt == SculptStage.Clay)
            {
                if (body == null || !MakerAtWork(maker)) { ReleaseMitts(body); ShowTools(false); KeepAtWork(maker); yield return null; continue; }
                // The head stays out of the block: if not, one small step back (twice at most).
                if (steps < 2 && HeadIntoMesh(body, block, blockMesh))
                {
                    steps++;
                    SculptSteps++;
                    ShowTools(false);
                    yield return LetGo(body);
                    yield return StepBack(maker, .03f);
                    continue;
                }
                ShowTools(true);
                handBusy = true;
                // Raise the mallet, then down onto the chisel's end.
                yield return Swing(body, maker, 0f, 1f, .16f);
                if (Sculpt != SculptStage.Clay) { handBusy = false; break; }
                yield return Swing(body, maker, 1f, 0f, .07f);
                Pats++;
                CarveTap(maker);
                yield return Swing(body, maker, 0f, 0f, .06f);
                handBusy = false;
                yield return new WaitForSeconds(PatGap + UnityEngine.Random.Range(0f, .12f));
            }
            handBusy = false;
        }

        // The chisel's edge on the face (at the corner being cut, low), the way it points (to Maker's right,
        // a little off the face and up), and Maker's right.
        private void ChiselLine(WindupCharacter maker, out Vector3 tip, out Vector3 dir, out Vector3 right, out Vector3 toward)
        {
            Vector3 at = maker.transform.position;
            toward = Vector3.ProjectOnPlane(block.position - at, Vector3.up);
            toward = toward.sqrMagnitude > 1e-6f ? toward.normalized : Vector3.forward;
            right = Vector3.Cross(Vector3.up, toward);
            // In the block's own units: its +z face is turned to Maker, Maker's left is its +x.
            float lift = Mathf.Min(.36f, .034f / Mathf.Max(blockHeight, .01f));
            tip = block.TransformPoint(new Vector3(.5f - chamfer - .07f, (Pats % 2 == 0 ? lift : lift * .7f), .52f));
            dir = (right * .93f - toward * .2f + Vector3.up * .3f).normalized;
        }

        // k = 0: the mallet's head on the chisel's end; k = 1: raised.
        private IEnumerator Swing(WindupBody body, WindupCharacter maker, float from, float to, float seconds)
        {
            for (float t = 0f; t < 1f; t += Time.deltaTime / seconds)
            {
                PlaceTools(body, maker, Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t)));
                yield return null;
            }
            PlaceTools(body, maker, to);
        }

        private void PlaceTools(WindupBody body, WindupCharacter maker, float raised)
        {
            if (body == null || block == null || chisel == null) return;
            ChiselLine(maker, out Vector3 tip, out Vector3 dir, out Vector3 right, out Vector3 toward);
            Vector3 butt = tip + dir * ChiselLength;
            chisel.SetPositionAndRotation(tip, Quaternion.LookRotation(dir, Vector3.up));
            body.Hold(false, new Pose(Reach(maker, tip + dir * .011f - toward * .004f), Quaternion.LookRotation(toward + Vector3.up * .3f, Vector3.up)));
            // The mallet's head: on the end, or back along the chisel and up.
            Vector3 head = butt + dir * (.011f + raised * .015f) + Vector3.up * raised * .014f;
            Vector3 handle = (right * .6f - toward * .3f - Vector3.up * .3f).normalized;
            Vector3 hand = Reach(maker, head + handle * MalletReach);
            Vector3 z = (head - hand).sqrMagnitude > 1e-8f ? (head - hand).normalized : toward;
            Vector3 x = Vector3.ProjectOnPlane(dir, z);
            x = x.sqrMagnitude > 1e-6f ? x.normalized : right;
            mallet.SetPositionAndRotation(hand, Quaternion.LookRotation(z, Vector3.Cross(z, x)));
            body.Hold(true, new Pose(hand, Quaternion.LookRotation(toward + Vector3.up * .3f, Vector3.up)));
            sculptFocus.position = tip;
        }

        // A tap: "tok", the corners cut a little deeper, a few chips fly off to the left.
        private void CarveTap(WindupCharacter maker)
        {
            ChiselLine(maker, out Vector3 tip, out Vector3 dir, out Vector3 right, out Vector3 toward);
            chamfer = Mathf.Min(ChamferMax, chamfer + ChamferPerTap);
            WoodBlock(blockMesh, chamfer);
            ToySounds.Play(ToySounds.Cue.Bonk, tip, .3f, 1.7f + UnityEngine.Random.Range(-.1f, .1f));
            ToySounds.Play(ToySounds.Cue.Press, tip, .18f, 2.6f);
            for (int i = 0; i < 2; i++)
                Chip(tip, -right * .2f + Vector3.up * .32f - toward * .06f + UnityEngine.Random.insideUnitSphere * .08f, .006f);
            if (Pats % 3 == 0) ToyFx.Puff(tip, .003f, 1);
        }

        // A small piece of wood flying off and landing on the desk (physics), gone after a while.
        private void Chip(Vector3 at, Vector3 velocity, float size)
        {
            if (chipMesh == null) { chipMesh = new Mesh { name = "WoodChip" }; WoodBlock(chipMesh, .3f, true); owned.Add(chipMesh); }
            var piece = Part("Wood Chip", chipMesh);
            piece.SetPositionAndRotation(at, UnityEngine.Random.rotation);
            piece.localScale = new Vector3(size, size * .45f, size * .75f);
            piece.gameObject.AddComponent<BoxCollider>().center = Vector3.up * .5f;
            var rb = piece.gameObject.AddComponent<Rigidbody>();
            rb.mass = .002f;
            rb.linearVelocity = velocity;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 20f;
            chips.Add(piece);
            Destroy(piece.gameObject, 2.4f);
            chips.RemoveAll(c => c == null);
            while (chips.Count > 16) { if (chips[0] != null) Destroy(chips[0].gameObject); chips.RemoveAt(0); }
        }

        private Mesh chipMesh;

        private void BuildCarvingTools()
        {
            if (chisel != null) return;
            // Long axis +z from the working end (0): chisel = blade then handle; mallet = handle then head.
            chisel = new GameObject("Chisel").transform;
            chisel.SetParent(transform, false);
            ToolStick(chisel, "Blade", .0064f, .0018f, 0f, .012f, new Color(.78f, .8f, .85f), .0008f);
            ToolStick(chisel, "Ring", .0062f, .0062f, .0105f, .0135f, new Color(.55f, .57f, .62f), .0028f);
            ToolStick(chisel, "Handle", .0058f, .0058f, .013f, ChiselLength, new Color(.8f, .55f, .3f), .0027f);
            mallet = new GameObject("Mallet").transform;
            mallet.SetParent(transform, false);
            ToolStick(mallet, "Handle", .0038f, .0038f, 0f, MalletReach - .004f, new Color(.8f, .55f, .3f), .0018f);
            var head = ToyMeshes.RoundedSlab(.022f, .013f, .006f, .013f, new Color(.7f, .47f, .25f), new Color(.55f, .36f, .18f), new Color(.7f, .47f, .25f), "MalletHead");
            owned.Add(head);
            var h = Part("Head", head);
            h.SetParent(mallet, false);
            h.localPosition = new Vector3(0f, 0f, MalletReach - .0065f);
            ShowTools(false);
        }

        // A rounded stick along +z from `from` to `to` (width along x, height along y).
        private void ToolStick(Transform parent, string name, float width, float height, float from, float to, Color colour, float radius)
        {
            var mesh = ToyMeshes.RoundedSlab(width, to - from, radius, height, colour, Color.Lerp(colour, Color.black, .2f), colour, name);
            owned.Add(mesh);
            var part = Part(name, mesh);
            part.SetParent(parent, false);
            part.localRotation = Quaternion.Euler(90f, 0f, 0f);
            part.localPosition = new Vector3(0f, height * .5f, (from + to) * .5f);
        }

        private void ShowTools(bool on)
        {
            if (chisel != null && chisel.gameObject.activeSelf != on) chisel.gameObject.SetActive(on);
            if (mallet != null && mallet.gameObject.activeSelf != on) mallet.gameObject.SetActive(on);
        }

        // The block: a unit box (1 across, 1 tall, on y = 0) whose four vertical corners are cut by `cut` (a part
        // of its size): side faces in grain strips, the cut corners fresh light wood, the top end grain.
        private static void WoodBlock(Mesh mesh, float cut, bool fresh = false)
        {
            cut = Mathf.Clamp(cut, .01f, .45f);
            float a = .5f, b = .5f - cut;
            Vector2[] ring =
            {
                new Vector2(a, -b), new Vector2(a, b), new Vector2(b, a), new Vector2(-b, a),
                new Vector2(-a, b), new Vector2(-a, -b), new Vector2(-b, -a), new Vector2(b, -a)
            };
            var v = new List<Vector3>();
            var c = new List<Color>();
            var t = new List<int>();
            void Tri(Vector3 p0, Vector3 p1, Vector3 p2, Color col, Vector3 outward)
            {
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) < 0f) { var s = p1; p1 = p2; p2 = s; }
                int n = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2);
                c.Add(col); c.Add(col); c.Add(col);
                t.Add(n); t.Add(n + 1); t.Add(n + 2);
            }
            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color col, Vector3 outward) { Tri(p0, p1, p2, col, outward); Tri(p0, p2, p3, col, outward); }
            const int strips = 5;
            for (int k = 0; k < 8; k++)
            {
                Vector2 p = ring[k], q = ring[(k + 1) % 8];
                Vector3 outward = new Vector3((p.x + q.x) * .5f, 0f, (p.y + q.y) * .5f);
                bool corner = k % 2 == 1;
                if (corner || fresh)
                {
                    Quad(new Vector3(p.x, 0f, p.y), new Vector3(q.x, 0f, q.y), new Vector3(q.x, 1f, q.y), new Vector3(p.x, 1f, p.y), WoodFresh, outward);
                    continue;
                }
                for (int s = 0; s < strips; s++)
                {
                    float y0 = s / (float)strips, y1 = (s + 1) / (float)strips;
                    Quad(new Vector3(p.x, y0, p.y), new Vector3(q.x, y0, q.y), new Vector3(q.x, y1, q.y), new Vector3(p.x, y1, p.y), s % 2 == 0 ? WoodColour : WoodGrain, outward);
                }
            }
            for (int k = 0; k < 8; k++)
            {
                Vector2 p = ring[k], q = ring[(k + 1) % 8];
                Tri(new Vector3(0f, 1f, 0f), new Vector3(p.x, 1f, p.y), new Vector3(q.x, 1f, q.y), fresh ? WoodFresh : WoodEnd, Vector3.up);
                Tri(new Vector3(0f, 0f, 0f), new Vector3(q.x, 0f, q.y), new Vector3(p.x, 0f, p.y), WoodDark, Vector3.down);
            }
            mesh.Clear();
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        // One mitten from `a` to `b` (eased), never beyond HandReach.
        private IEnumerator Mitt(WindupBody body, WindupCharacter maker, bool right, Vector3 a, Vector3 b, Quaternion grip, float seconds)
        {
            for (float t = 0f; t < 1f; t += Time.deltaTime / seconds)
            {
                body.Hold(right, new Pose(Reach(maker, Vector3.Lerp(a, b, Mathf.SmoothStep(0f, 1f, t))), grip));
                yield return null;
            }
            body.Hold(right, new Pose(Reach(maker, b), grip));
        }

        private static Vector3 Reach(WindupCharacter maker, Vector3 p)
        {
            Vector3 at = maker.transform.position;
            Vector3 flat = Vector3.ProjectOnPlane(p - at, Vector3.up);
            if (flat.magnitude <= HandReach) return p;
            return at + flat.normalized * HandReach + Vector3.up * Vector3.Dot(p - at, Vector3.up);
        }

        // Lets go of what the mittens held and gives them a moment to come back to the body before it walks
        // (a held pose stays where it was in the room: walking off with it would stretch the arm-less hand).
        private static IEnumerator LetGo(WindupBody body)
        {
            ReleaseMitts(body);
            yield return new WaitForSeconds(.22f);
        }

        private static void ReleaseMitts(WindupBody body)
        {
            if (body == null) return;
            body.Hold(true, null);
            body.Hold(false, null);
        }

        // Held, or put down away from the work: back to the stand point once it stands again.
        private void KeepAtWork(WindupCharacter maker)
        {
            if (maker == null || !sculptReachable || Time.time < nextStandCheck || workCarried) return; // F89: not while the stand carries the work
            nextStandCheck = Time.time + 1.5f;
            var pet = maker.movement;
            if (pet.IsHeld || (pet.State != Pet.MotionState.Idle && !pet.MeetingSeatLocked)) return;
            if (MakerAtWork(maker)) { if (!pet.MeetingSeatLocked) pet.LockMeetingSeat(); return; }
            pet.UnlockMeetingSeat();
            pet.GoTo(standPoint, 3f, true);
        }

        private IEnumerator StepBack(WindupCharacter maker, float by)
        {
            var pet = maker.movement;
            Vector3 dir = Vector3.ProjectOnPlane(standPoint - sculptSpot, Vector3.up).normalized;
            var room = arrival.handoff.meeting.environment;
            if (!room.TrySameLevelNavPoint(standPoint + dir * by, out var nav)) yield break;
            standPoint = nav;
            pet.UnlockMeetingSeat();
            yield return null;
            if (pet.GoTo(standPoint, .5f, true))
                for (float until = Time.time + 3f; Time.time < until && pet.State != Pet.MotionState.Idle; ) yield return null;
            for (int i = 0; i < 30 && !pet.MeetingSeatLocked && !pet.IsHeld; i++) { pet.LockMeetingSeat(); yield return null; }
        }

        // Points spread evenly over a mesh's surface, with their face normals (Meshy's smart-topology models are
        // low-poly: a cylinder's side has vertices only at its rims, so the vertices alone miss whole faces).
        public sealed class Surface { public Vector3[] points, normals; }
        private readonly Dictionary<Mesh, Surface> surfaces = new Dictionary<Mesh, Surface>();
        public const int SurfaceSamples = 900;
        public Surface SurfaceOf(Mesh mesh)
        {
            if (mesh == null) return null;
            if (surfaces.TryGetValue(mesh, out var s)) return s;
            s = Sample(mesh, SurfaceSamples);
            if (surfaces.Count > 8) surfaces.Clear();
            surfaces[mesh] = s;
            return s;
        }

        public static Surface Sample(Mesh mesh, int count)
        {
            var v = mesh.vertices;
            var t = mesh.triangles;
            int tris = t.Length / 3;
            var area = new float[tris];
            float total = 0f;
            for (int i = 0; i < tris; i++)
            {
                total += Vector3.Cross(v[t[i * 3 + 1]] - v[t[i * 3]], v[t[i * 3 + 2]] - v[t[i * 3]]).magnitude * .5f;
                area[i] = total;
            }
            var s = new Surface { points = new Vector3[count], normals = new Vector3[count] };
            var random = new System.Random(7);
            for (int k = 0; k < count && total > 0f; k++)
            {
                int i = System.Array.BinarySearch(area, (float)random.NextDouble() * total);
                if (i < 0) i = ~i;
                i = Mathf.Min(i, tris - 1);
                Vector3 a = v[t[i * 3]], b = v[t[i * 3 + 1]], c = v[t[i * 3 + 2]];
                float u = (float)random.NextDouble(), w = (float)random.NextDouble();
                if (u + w > 1f) { u = 1f - u; w = 1f - w; }
                s.points[k] = a + (b - a) * u + (c - a) * w;
                s.normals[k] = Vector3.Cross(b - a, c - a).normalized;
            }
            return s;
        }

        // Is the head (its ellipsoid, with a margin) into the block or the model? Checked against points all
        // over their real surface.
        public const float HeadMarginWork = 1.2f;   // ellipsoid radii: the head stays a fifth of its size away
        private bool HeadIntoMesh(WindupBody body, Transform thing, Mesh mesh, float margin = HeadMarginWork)
        {
            if (body == null || thing == null || mesh == null) return false;
            foreach (var p in SurfaceOf(mesh).points)
                if (body.HeadDepth(thing.TransformPoint(p)) < margin) return true;
            return false;
        }

        // How far from `spot` Maker has to stand (on `dir`, facing the spot) for its head - posed as it is now,
        // relative to its body - to stay HeadMarginStand of its size clear of `mesh` placed at spot/turn.
        // One walk to the right distance instead of many small steps back.
        // The head is tested in the poses it takes while working: as it is, looking down at the work (with the
        // lean), turned toward a dab on either side, and lifted by the little jump of a cheer.
        public const float HeadMarginStand = 1.15f;
        private static readonly (Vector3 euler, float lift)[] WorkPoses =
        {
            (Vector3.zero, 0f), (new Vector3(30f, 0f, 0f), 0f), (new Vector3(30f, 35f, 0f), 0f), (new Vector3(30f, -35f, 0f), 0f),
            (new Vector3(10f, 0f, 0f), .028f)
        };
        private float ClearStand(WindupCharacter maker, Mesh mesh, Vector3 spot, Quaternion turn, Vector3 scale, Vector3 dir, float from)
        {
            var body = WindupBody.Of(maker);
            if (body == null || body.headBone == null || mesh == null) return from;
            var headInBody = body.transform.worldToLocalMatrix * body.headBone.localToWorldMatrix;
            Vector3 pivot = headInBody.GetColumn(3);
            var poses = new Matrix4x4[WorkPoses.Length];
            for (int i = 0; i < poses.Length; i++)
                poses[i] = Matrix4x4.Translate(pivot + Vector3.up * WorkPoses[i].lift) * Matrix4x4.Rotate(Quaternion.Euler(WorkPoses[i].euler)) * Matrix4x4.Translate(-pivot) * headInBody;
            var points = SurfaceOf(mesh).points;
            var place = Matrix4x4.TRS(spot, turn, scale);
            var world = new Vector3[points.Length];
            for (int i = 0; i < points.Length; i++) world[i] = place.MultiplyPoint3x4(points[i]);
            float y = maker.transform.position.y;
            for (float d = from; d < from + .2f; d += .005f)
            {
                var root = Matrix4x4.TRS(new Vector3(spot.x, y, spot.z) + dir * d, Quaternion.LookRotation(-dir, Vector3.up), Vector3.one);
                bool clear = true;
                foreach (var pose in poses)
                {
                    var toHead = (root * pose).inverse;
                    foreach (var p in world)
                        if (body.HeadDepthInHead(toHead.MultiplyPoint3x4(p)) < HeadMarginStand) { clear = false; break; }
                    if (!clear) break;
                }
                if (clear) return d;
            }
            return from + .2f;
        }

        // Steps back (a centimetre at a time) until the head is clear of `thing`, at most `most` steps.
        private IEnumerator ClearHead(WindupCharacter maker, Transform thing, Mesh mesh, int most)
        {
            var body = WindupBody.Of(maker);
            for (int i = 0; i < most && Sculpt != SculptStage.None && HeadIntoMesh(body, thing, mesh); i++)
            {
                SculptSteps++;
                yield return LetGo(body);
                // (More than the walking agent's stopping distance, 2.5 cm, or it would not move at all.)
                yield return StepBack(maker, .03f);
                yield return new WaitForSeconds(.5f); // turned back to the work, the head looking at it again
            }
        }

        // ------------------------------------------------------------ 2. the shape arrives

        // `rough`: Meshy's untextured mesh (any size); shown at `widthMm` wide in block colour.
        private IEnumerator ShapeArrived(Mesh rough, float widthMm)
        {
            if (Sculpt == SculptStage.None) yield break;
            // The shape can come before Maker has even reached the block: it gets there and gives it a tap or
            // two first, so the block is seen to be worked before the shape comes out of it.
            for (float until = Time.time + 6f; (Sculpt == SculptStage.Clay || Sculpt == SculptStage.Approach) && Pats < 2 && sculptReachable && Time.time < until; ) yield return null;
            Sculpt = SculptStage.Shape;
            // Let the tap in progress finish, then the tools go.
            for (float until = Time.time + .6f; handBusy && Time.time < until; ) yield return null;
            yield return new WaitForSeconds(.2f);
            var maker = cast.TryGetValue(Crew.Maker, out var m) ? m : null;
            if (chisel != null && chisel.gameObject.activeSelf) ToyFx.Puff(chisel.position, .004f, 2);
            ShowTools(false);
            yield return LetGo(WindupBody.Of(maker));
            if (viewMesh != null) { owned.Remove(viewMesh); Destroy(viewMesh); }
            viewMesh = Scaled(rough, widthMm, WoodColour);
            owned.Add(viewMesh);
            Vector3 size = viewMesh.bounds.size;
            // The model's final place is known now (not shown yet): Maker first makes room for it - beside
            // it, its head clear of its real surface - then the block grows into it.
            if (view == null) { view = Part("Sculpt View", viewMesh); view.SetParent(transform, false); }
            view.gameObject.SetActive(false);
            view.GetComponent<MeshFilter>().sharedMesh = viewMesh;
            view.GetComponent<MeshRenderer>().sharedMaterial = material;
            view.SetPositionAndRotation(sculptSpot, sculptTurn);
            view.localScale = Vector3.one;
            if (maker != null && sculptReachable)
            {
                // One walk back to where the head stays clear of the whole model (none if it already is).
                float radius = Mathf.Max(size.x, size.z) * .5f;
                Vector3 off = Vector3.ProjectOnPlane(maker.transform.position - sculptSpot, Vector3.up);
                Vector3 dir = off.sqrMagnitude > 1e-6f ? off.normalized : Vector3.ProjectOnPlane(standPoint - sculptSpot, Vector3.up).normalized;
                // ... and of the block grown around it (its corners reach out further than the shape).
                float need = Mathf.Max(radius + .04f, ClearStand(maker, viewMesh, sculptSpot, sculptTurn, Vector3.one, dir, radius + .04f));
                need = Mathf.Max(need, ClearStand(maker, blockMesh, sculptSpot, blockTurn, GrownBlock(size), dir, radius + .04f));
                if (need > off.magnitude + .005f) yield return StepBack(maker, Mathf.Max(.03f, need - off.magnitude));
            }
            // The block grows to just around the shape, big chunks fall off ("tuk tuk") and the shape stands
            // there in wood; Maker blows the dust away, Fixer nods.
            float foot = GrownBlock(size).x;
            if (block != null) yield return ScaleTo(block, GrownBlock(size), .3f);
            view.gameObject.SetActive(true);
            view.localScale = Vector3.one;
            if (block != null) block.gameObject.SetActive(false);
            for (int k = 0; k < 6; k++)
            {
                float angle = k * 60f + 30f;
                Vector3 out_ = blockTurn * (Quaternion.Euler(0f, angle, 0f) * Vector3.forward);
                Vector3 at = sculptSpot + out_ * foot * .42f + Vector3.up * size.y * (k % 2 == 0 ? .62f : .18f);
                StartCoroutine(Chunk(at, blockTurn * Quaternion.Euler(0f, angle, 0f), new Vector3(foot * .34f, size.y * .32f, foot * .22f), out_ * .16f + Vector3.up * .14f));
                Chunks++;
            }
            ToyFx.Puff(sculptSpot + Vector3.up * size.y * .5f, .014f, 5);
            ToySounds.Play(ToySounds.Cue.Bonk, sculptSpot, .5f, .8f);
            StartCoroutine(After(.16f, () => ToySounds.Play(ToySounds.Cue.Bonk, sculptSpot, .4f, .72f)));
            yield return new WaitForSeconds(.45f);
            if (maker != null) yield return BlowDust(maker, size.y);
            Body(Crew.Fixer)?.PlaySoon("React_Nod", .3f);
        }

        // The block just around a shape of this size.
        private static Vector3 GrownBlock(Vector3 size)
        {
            float foot = Mathf.Min(Mathf.Max(size.x, size.z) * 1.08f, .17f);
            return new Vector3(foot, Mathf.Min(size.y * 1.05f, .17f), foot);
        }

        // A piece of the block falling off the shape: it tips away, lands and shrinks out of sight.
        private IEnumerator Chunk(Vector3 at, Quaternion turn, Vector3 size, Vector3 velocity)
        {
            if (chipMesh == null) { chipMesh = new Mesh { name = "WoodChip" }; WoodBlock(chipMesh, .3f, true); owned.Add(chipMesh); }
            var piece = Part("Wood Chunk", blockMesh != null ? blockMesh : chipMesh);
            piece.SetPositionAndRotation(at - Vector3.up * size.y * .5f, turn);
            piece.localScale = size;
            piece.gameObject.AddComponent<BoxCollider>();
            var rb = piece.gameObject.AddComponent<Rigidbody>();
            rb.mass = .02f;
            rb.linearVelocity = velocity;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 6f;
            yield return new WaitForSeconds(1.1f);
            if (piece == null) yield break;
            yield return ScaleTo(piece, Vector3.zero, .3f);
            if (piece != null) Destroy(piece.gameObject);
        }

        // "Hoo": three little puffs from Maker's face to the made thing.
        private IEnumerator BlowDust(WindupCharacter maker, float height)
        {
            var body = WindupBody.Of(maker);
            body?.Emote(WindupBody.Blink, .7f);
            Vector3 mouth = body != null ? body.HeadTop - Vector3.up * .075f : maker.transform.position + Vector3.up * .08f;
            Vector3 to = sculptSpot + Vector3.up * height * .6f;
            Vector3 dir = Vector3.ProjectOnPlane(to - mouth, Vector3.up).normalized;
            mouth += dir * .045f;
            ToySounds.Play(ToySounds.Cue.Whoosh, mouth, .25f, 1.35f);
            for (int i = 0; i < 3; i++)
            {
                ToyFx.Puff(Vector3.Lerp(mouth, to, .25f + i * .25f), .005f + i * .002f, 2);
                yield return new WaitForSeconds(.12f);
            }
        }

        // ------------------------------------------------------------ 3. paint (F35: a big brush, wide bands)

        public const int BandsPerSide = 3;
        public const float BandWidth = .013f, DabStep = .0035f, StrokeSpeed = .1f;
        private int rowCursor;
        private Vector3 lastTip;
        private readonly List<Mesh> bandMeshes = new List<Mesh>();

        private IEnumerator PaintModel(WindupCharacter maker)
        {
            Sculpt = SculptStage.Paint;
            Bands = Dips = 0;
            rowCursor = 0;
            var body = WindupBody.Of(maker);
            paintColours = PaintColours().Select(AgainstWood).ToArray();
            BuildBrush(paintColours[0]);
            makerBrush.gameObject.SetActive(false); // shown once it is in the mitten (not while Maker walks)
            lastTip = maker.transform.position + Vector3.up * .04f + Vector3.ProjectOnPlane(sculptSpot - maker.transform.position, Vector3.up).normalized * .05f;
            Say(Crew.Maker, "Now the colours!");
            int clears = 0, sideBands = 0, misses = 0, sides = 0;
            // Like a painter, it works one side, then walks round to the next: each side is one where the
            // big head stays clear of the model and the brush reaches enough of its surface. The pot comes along.
            if (sculptReachable) { yield return MoveToPaintSide(maker, 0f); sides++; }
            PlacePot(maker);
            while (Sculpt == SculptStage.Paint)
            {
                if (body == null || !MakerAtWork(maker)) { ReleaseMitts(body); makerBrush.gameObject.SetActive(false); KeepAtWork(maker); yield return null; continue; }
                makerBrush.gameObject.SetActive(true);
                // Looking down at the work can bring the big head to the model: a step back first.
                if (clears < 1 && HeadIntoMesh(body, view, viewMesh)) { clears++; makerBrush.gameObject.SetActive(false); yield return ClearHead(maker, view, viewMesh, 1); PlacePot(maker); continue; }
                if ((sideBands >= BandsPerSide || misses >= 3) && sides < 4)
                {
                    makerBrush.gameObject.SetActive(false);
                    yield return LetGo(body);
                    // The other side the person can see (F35), else on round the model.
                    yield return MoveToPaintSide(maker, sides % 2 == 1 ? TurnToMirrorSide(maker) : 70f);
                    sides++;
                    sideBands = misses = 0;
                    PlacePot(maker);
                    continue;
                }
                if (!PickBand(maker, out var band))
                {
                    // Nothing in reach on this side: the brush stays ready, Maker looks the work over.
                    misses++;
                    PlaceBrush(body, maker, sculptSpot + Vector3.up * .03f + Vector3.ProjectOnPlane(maker.transform.position - sculptSpot, Vector3.up).normalized * .03f, Vector3.up, 1f);
                    yield return new WaitForSeconds(.4f);
                    continue;
                }
                Color colour = paintColours[(Bands / 2) % paintColours.Length];
                handBusy = true;
                yield return Dip(body, maker, colour);
                if (Sculpt != SculptStage.Paint) { handBusy = false; break; }
                yield return Stroke(body, maker, band, colour);
                handBusy = false;
                Bands++;
                sideBands++;
                yield return new WaitForSeconds(.12f + UnityEngine.Random.Range(0f, .2f));
            }
            handBusy = false;
        }

        // Moves the brush's tip from where it is to `to` (eased), the brush leaning off `normal`.
        private IEnumerator TipTo(WindupBody body, WindupCharacter maker, Vector3 to, Vector3 normal, float seconds)
        {
            Vector3 from = lastTip;
            for (float t = 0f; t < 1f; t += Time.deltaTime / seconds)
            {
                PlaceBrush(body, maker, Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t)), normal, 1f);
                yield return null;
            }
            PlaceBrush(body, maker, to, normal, 1f);
        }

        // Into the pot ("plop"), a little stir, out: the bristles take the band's colour (so does the pot).
        private IEnumerator Dip(WindupBody body, WindupCharacter maker, Color colour)
        {
            if (pot == null || !pot.gameObject.activeSelf) { SetBrushColour(colour); yield break; }
            Vector3 above = pot.position + Vector3.up * .045f, into = pot.position + Vector3.up * .013f;
            yield return TipTo(body, maker, above, Vector3.up, .22f);
            yield return TipTo(body, maker, into, Vector3.up, .12f);
            Dips++;
            SetBrushColour(colour);
            SetPotColour(colour);
            ToySounds.Play(ToySounds.Cue.Drip, into, .35f, .9f);
            Vector3 toward = Vector3.ProjectOnPlane(sculptSpot - maker.transform.position, Vector3.up).normalized, right = Vector3.Cross(Vector3.up, toward);
            for (float t = 0f; t < 1f; t += Time.deltaTime / .24f)
            {
                float a = t * Mathf.PI * 2f;
                PlaceBrush(body, maker, into + (right * Mathf.Cos(a) + toward * Mathf.Sin(a)) * .004f, Vector3.up, 1f);
                yield return null;
            }
            yield return TipTo(body, maker, above, Vector3.up, .15f);
        }

        // A band: the model's real surface points on Maker's side (facing it, within reach) at one height,
        // ordered around the model so the brush can sweep across them. Rows go up the model in turn.
        private bool PickBand(WindupCharacter maker, out List<(Vector3 p, Vector3 n)> band)
        {
            band = null;
            if (view == null || viewMesh == null) return false;
            var surface = SurfaceOf(viewMesh);
            Vector3 at = maker.transform.position;
            var bounds = view.GetComponent<Renderer>().bounds;
            float bottom = Mathf.Max(bounds.min.y + .006f, at.y + .008f), top = Mathf.Min(bounds.max.y - .004f, at.y + .09f);
            if (top - bottom < .004f) return false;
            int rows = Mathf.Max(1, Mathf.FloorToInt((top - bottom) / BandWidth));
            Vector3 toMaker = Vector3.ProjectOnPlane(at - sculptSpot, Vector3.up).normalized;
            for (int tries = 0; tries < rows; tries++)
            {
                int row = rowCursor++ % rows;
                float y = bottom + (row + .5f) * (top - bottom) / rows;
                var picked = new List<(Vector3 p, Vector3 n, float angle)>();
                for (int i = 0; i < surface.points.Length; i++)
                {
                    Vector3 p = view.TransformPoint(surface.points[i]);
                    if (Mathf.Abs(p.y - y) > BandWidth * .5f) continue;
                    Vector3 n = view.TransformDirection(surface.normals[i]).normalized;
                    if (!Paintable(at, p, n)) continue;
                    float angle = Vector3.SignedAngle(toMaker, Vector3.ProjectOnPlane(p - sculptSpot, Vector3.up), Vector3.up);
                    picked.Add((p + Vector3.up * (y - p.y), n, angle));
                }
                if (picked.Count < 4) continue;
                picked.Sort((a, b) => a.angle.CompareTo(b.angle));
                // Alternate the sweep's direction, like a painter going back and forth.
                if (Bands % 2 == 1) picked.Reverse();
                band = new List<(Vector3, Vector3)>();
                foreach (var q in picked)
                    if (band.Count == 0 || Vector3.Distance(band[band.Count - 1].p, q.p) >= .003f) band.Add((q.p, q.n));
                if (band.Count >= 3) return true;
            }
            return false;
        }

        // One sweep along the band: the tip goes on the surface at the start, travels the band at StrokeSpeed
        // ("swish"), leaving overlapping dabs (one band mesh), and lifts off; every other band leaves a drip.
        private IEnumerator Stroke(WindupBody body, WindupCharacter maker, List<(Vector3 p, Vector3 n)> band, Color colour)
        {
            var go = new GameObject("Paint Band", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(view, false);
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var mesh = new Mesh { name = "PaintBand" };
            mesh.MarkDynamic();
            bandMeshes.Add(mesh);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            dots.Add(go.transform);
            var verts = new List<Vector3>();
            var cols = new List<Color>();
            var norms = new List<Vector3>();
            var tris = new List<int>();
            float lift = .0009f + Mathf.Min(Bands, 8) * .00025f; // a later band lies on top of an earlier one
            Vector3 n0 = band[0].n;
            yield return TipTo(body, maker, band[0].p + n0 * .012f, n0, .16f);
            yield return TipTo(body, maker, band[0].p + n0 * .0015f, n0, .08f);
            ToySounds.Play(ToySounds.Cue.Brush, band[0].p, .4f, .85f + UnityEngine.Random.Range(0f, .2f));
            int seg = 0;
            float along = 0f, sinceDab = DabStep;
            Vector3 tip = band[0].p, normal = n0;
            while (seg < band.Count - 1 && Sculpt == SculptStage.Paint)
            {
                float step = StrokeSpeed * Time.deltaTime;
                while (step > 0f && seg < band.Count - 1)
                {
                    Vector3 a = band[seg].p, b = band[seg + 1].p;
                    float len = Vector3.Distance(a, b), left = len - along;
                    float go_ = Mathf.Min(step, left);
                    along += go_;
                    step -= go_;
                    sinceDab += go_;
                    float k = len > 1e-6f ? along / len : 1f;
                    tip = Vector3.Lerp(a, b, k);
                    normal = Vector3.Slerp(band[seg].n, band[seg + 1].n, k).normalized;
                    if (sinceDab >= DabStep)
                    {
                        sinceDab = 0f;
                        AddDab(verts, cols, norms, tris, tip, normal, b - a, colour, lift);
                    }
                    if (along >= len - 1e-6f) { seg++; along = 0f; }
                }
                mesh.Clear();
                mesh.SetVertices(verts);
                mesh.SetColors(cols);
                mesh.SetNormals(norms);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();
                PlaceBrush(body, maker, tip + normal * .0015f, normal, 1f);
                yield return null;
            }
            if (Bands % 2 == 1 && verts.Count > 0) StartCoroutine(Drip(tip, normal, colour, lift));
            yield return TipTo(body, maker, tip + normal * .014f + Vector3.up * .006f, normal, .14f);
        }

        // An oval of paint on the surface at `p` (in the view's space), longer along the stroke.
        private void AddDab(List<Vector3> verts, List<Color> cols, List<Vector3> norms, List<int> tris, Vector3 p, Vector3 n, Vector3 along, Color colour, float lift)
        {
            if (verts.Count > 6000) return;
            Vector3 ln = view.InverseTransformDirection(n).normalized;
            Vector3 c = view.InverseTransformPoint(p) + ln * lift;
            Vector3 t = Vector3.ProjectOnPlane(view.InverseTransformDirection(along), ln);
            if (t.sqrMagnitude < 1e-10f) t = Vector3.ProjectOnPlane(Vector3.right, ln);
            if (t.sqrMagnitude < 1e-10f) t = Vector3.ProjectOnPlane(Vector3.forward, ln);
            t.Normalize();
            Vector3 b = Vector3.Cross(ln, t);
            float shade = UnityEngine.Random.Range(.95f, 1.04f);
            Color col = new Color(Mathf.Clamp01(colour.r * shade), Mathf.Clamp01(colour.g * shade), Mathf.Clamp01(colour.b * shade), 1f);
            int first = verts.Count;
            verts.Add(c); cols.Add(col); norms.Add(ln);
            const int sides = 9;
            for (int k = 0; k < sides; k++)
            {
                float a = k * Mathf.PI * 2f / sides;
                verts.Add(c + t * Mathf.Cos(a) * .0078f + b * Mathf.Sin(a) * (BandWidth * .5f));
                cols.Add(col);
                norms.Add(ln);
            }
            bool flip = Vector3.Dot(Vector3.Cross(verts[first + 1] - c, verts[first + 2] - c), ln) < 0f;
            for (int k = 0; k < sides; k++)
            {
                int i1 = first + 1 + k, i2 = first + 1 + (k + 1) % sides;
                tris.Add(first);
                tris.Add(flip ? i2 : i1);
                tris.Add(flip ? i1 : i2);
            }
            Dabs++;
        }

        // A drop running a little way down from the end of a band ("tip").
        private IEnumerator Drip(Vector3 p, Vector3 n, Color colour, float lift)
        {
            if (view == null) yield break;
            Vector3 down = Vector3.ProjectOnPlane(Vector3.down, n);
            if (down.sqrMagnitude < 1e-4f) yield break; // on a top: no run
            down.Normalize();
            var outline = new List<Vector2>();
            for (int i = 0; i <= 12; i++)
            {
                float a = Mathf.PI + i * Mathf.PI / 12f; // the round end at the top
                outline.Add(new Vector2(Mathf.Cos(a) * .0026f, -Mathf.Sin(a) * .0026f));
            }
            outline.Add(new Vector2(.0016f, -.009f));
            outline.Add(new Vector2(0f, -.0115f));
            outline.Add(new Vector2(-.0016f, -.009f));
            var mesh = new Mesh { name = "PaintDrip" };
            var v = new List<Vector3> { Vector3.zero };
            foreach (var q in outline) v.Add(new Vector3(q.x, q.y, 0f));
            var tri = new List<int>();
            for (int i = 1; i < v.Count; i++)
            {
                int j = i + 1 < v.Count ? i + 1 : 1;
                bool front = Vector3.Dot(Vector3.Cross(v[i] - v[0], v[j] - v[0]), Vector3.back) > 0f;
                tri.Add(0); tri.Add(front ? i : j); tri.Add(front ? j : i);
            }
            mesh.SetVertices(v);
            mesh.SetColors(Enumerable.Repeat(colour, v.Count).ToList());
            mesh.SetNormals(Enumerable.Repeat(Vector3.back, v.Count).ToList());
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            bandMeshes.Add(mesh);
            var drip = Part("Paint Drip", mesh);
            drip.SetParent(view, true);
            drip.SetPositionAndRotation(p + n * (lift + .0004f), Quaternion.LookRotation(-n, -down));
            dots.Add(drip);
            ToySounds.Play(ToySounds.Cue.Drip, p, .12f, 1.6f);
            for (float t = 0f; t < 1f && drip != null; t += Time.deltaTime / .7f)
            {
                drip.localScale = new Vector3(1f, Mathf.Lerp(.25f, 1f, Mathf.SmoothStep(0f, 1f, t)), 1f);
                yield return null;
            }
        }

        // From a body standing at `at`: the point faces it (or faces up), is between its feet and its head,
        // and the mitten holding the brush to it stays within HandReach.
        private static bool Paintable(Vector3 at, Vector3 p, Vector3 n)
        {
            Vector3 toMaker = Vector3.ProjectOnPlane(at - p, Vector3.up);
            if (Vector3.Dot(n, toMaker.normalized) < .25f && n.y < .6f) return false;
            float y = p.y - at.y;
            if (y < .006f || y > .09f) return false;
            Vector3 hand = BrushHand(at, p + n * .002f, n);
            return Vector3.ProjectOnPlane(hand - at, Vector3.up).magnitude <= HandReach && Vector3.Distance(hand, p) <= BrushLength + .006f;
        }

        // The mitten holds the brush near its end; the brush leans out of the surface, toward Maker and up.
        private static Vector3 BrushHand(WindupCharacter maker, Vector3 tip, Vector3 normal) => BrushHand(maker.transform.position, tip, normal);
        // F35: the mitten holding the (longer) brush comes from Maker's right side at chest height, never from in
        // front of its face: the brush points from the tip toward that side, and the hand stays below the head
        // (BrushHandTop), out of the body (BrushHandOut) and within HandReach.
        public const float BrushHandTop = .062f, BrushHandOut = .034f;
        private static Vector3 BrushHand(Vector3 makerAt, Vector3 tip, Vector3 normal)
        {
            Vector3 toward = Vector3.ProjectOnPlane(tip - makerAt, Vector3.up);
            toward = toward.sqrMagnitude > 1e-6f ? toward.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, toward);
            Vector3 side = makerAt + right * .042f + toward * .022f + Vector3.up * .045f;
            Vector3 dir = side - tip;
            dir = dir.sqrMagnitude > 1e-8f ? dir.normalized : (normal + Vector3.up).normalized;
            Vector3 hand = tip + dir * BrushLength;
            hand.y = Mathf.Min(hand.y, makerAt.y + BrushHandTop);
            Vector3 flat = Vector3.ProjectOnPlane(hand - makerAt, Vector3.up);
            if (flat.magnitude < BrushHandOut) flat = (flat.sqrMagnitude > 1e-8f ? flat.normalized : right) * BrushHandOut;
            return makerAt + flat + Vector3.up * (hand.y - makerAt.y);
        }

        // Round the model to a side (starting `turn` degrees on from where Maker is) where its head stays clear
        // and at least a few surface points are in reach; walked in short arcs, never through the model.
        private IEnumerator MoveToPaintSide(WindupCharacter maker, float turn)
        {
            if (view == null || viewMesh == null || maker == null) yield break;
            var room = arrival.handoff.meeting.environment;
            var surface = SurfaceOf(viewMesh);
            var world = new Vector3[surface.points.Length];
            var normals = new Vector3[surface.points.Length];
            for (int i = 0; i < world.Length; i++) { world[i] = view.TransformPoint(surface.points[i]); normals[i] = view.TransformDirection(surface.normals[i]).normalized; }
            Vector3 size = viewMesh.bounds.size;
            float radius = Mathf.Max(size.x, size.z) * .5f;
            Vector3 now = Vector3.ProjectOnPlane(maker.transform.position - sculptSpot, Vector3.up);
            float startDeg = Mathf.Atan2(now.z, now.x) * Mathf.Rad2Deg;
            foreach (float offset in new[] { 0f, 55f, -55f, 110f, -110f, 180f })
            {
                float deg = startDeg + turn + offset;
                Vector3 dir = new Vector3(Mathf.Cos(deg * Mathf.Deg2Rad), 0f, Mathf.Sin(deg * Mathf.Deg2Rad));
                float d = ClearStand(maker, viewMesh, sculptSpot, sculptTurn, Vector3.one, dir, radius + .03f) + .004f;
                if (!room.TrySameLevelNavPoint(sculptSpot + dir * d, out var nav) || Mathf.Abs(nav.y - maker.transform.position.y) > .04f) continue;
                int reachable = 0;
                for (int i = 0; i < world.Length && reachable < 8; i++) if (Paintable(nav, world[i], normals[i])) reachable++;
                if (reachable < 8 || !MakerStandClear(nav) || !Walkable(maker.transform.position, nav, 2.5f)) continue;
                if (Vector3.ProjectOnPlane(nav - maker.transform.position, Vector3.up).magnitude < .02f) { standPoint = nav; NoteWorkAngle(); yield break; }
                // Round the model in arcs of at most 60 degrees, at the stand distance.
                var pet = maker.movement;
                float from = startDeg, to = deg;
                int legs = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(Mathf.DeltaAngle(from, to)) / 60f));
                pet.UnlockMeetingSeat();
                yield return null;
                for (int leg = 1; leg <= legs && Sculpt == SculptStage.Paint; leg++)
                {
                    float a = from + Mathf.DeltaAngle(from, to) * leg / legs;
                    Vector3 legDir = new Vector3(Mathf.Cos(a * Mathf.Deg2Rad), 0f, Mathf.Sin(a * Mathf.Deg2Rad));
                    Vector3 legPoint = leg == legs ? nav : sculptSpot + legDir * Mathf.Max(d, now.magnitude);
                    if (leg < legs && !room.TrySameLevelNavPoint(legPoint, out legPoint)) continue;
                    if (!MakerStandClear(legPoint)) continue;
                    if (pet.GoTo(legPoint, 1f, true))
                        for (float until = Time.time + 5f; Time.time < until && pet.State != Pet.MotionState.Idle && !pet.IsHeld; ) yield return null;
                }
                standPoint = nav;
                NoteWorkAngle();
                for (int i = 0; i < 30 && !pet.MeetingSeatLocked && !pet.IsHeld; i++) { pet.LockMeetingSeat(); yield return null; }
                yield return new WaitForSeconds(.3f); // turned to the work
                yield break;
            }
        }

        private void PlaceBrush(WindupBody body, WindupCharacter maker, Vector3 tip, Vector3 normal, float weight)
        {
            Vector3 hand = Reach(maker, BrushHand(maker, tip, normal));
            Vector3 along = (tip - hand).sqrMagnitude > 1e-8f ? (tip - hand).normalized : Vector3.down;
            // The brush runs through the mitten to the tip: nearer than its length, the handle shows behind the
            // mitten (the tip never goes into the surface); farther, it stops short.
            float reach = Vector3.Distance(hand, tip);
            makerBrush.SetPositionAndRotation(reach < BrushLength ? tip - along * BrushLength : hand, Quaternion.LookRotation(along, Vector3.up));
            body.Hold(true, new Pose(hand, Quaternion.LookRotation(Vector3.ProjectOnPlane(hand - maker.transform.position, Vector3.up).normalized + Vector3.up * .3f, Vector3.up)));
            body.Hold(false, null);
            sculptFocus.position = tip;
            lastTip = tip;
        }

        // F35: a brush that reads as one from across the desk (it used to be three tiny round discs in a row):
        // a long wooden handle, a metal ferrule, a pale bristle tuft and its wet, coloured end. +z runs from the
        // mitten (0) to the tip (BrushLength).
        private void BuildBrush(Color colour)
        {
            if (makerBrush != null) { SetBrushColour(colour); return; }
            makerBrush = new GameObject("Paint Brush").transform;
            makerBrush.SetParent(transform, false);
            ToolStick(makerBrush, "Handle", .0052f, .0052f, -.006f, BrushLength * .66f, new Color(.78f, .53f, .3f), .0024f);
            ToolStick(makerBrush, "Ferrule", .0068f, .0068f, BrushLength * .64f, BrushLength * .76f, new Color(.8f, .81f, .85f), .0026f);
            ToolStick(makerBrush, "Bristles", .0082f, .0074f, BrushLength * .75f, BrushLength * .9f, new Color(.96f, .9f, .78f), .0034f);
            brushColour = Color.clear;
            SetBrushColour(colour);
            makerBrush.gameObject.SetActive(false);
        }

        private Color brushColour = Color.clear;
        private void SetBrushColour(Color colour)
        {
            if (makerBrush == null || colour == brushColour) return;
            brushColour = colour;
            var old = makerBrush.Find("Tip");
            if (old != null) Destroy(old.gameObject);
            ToolStick(makerBrush, "Tip", .0086f, .0078f, BrushLength * .86f, BrushLength, colour, .0037f);
        }

        // The paint pot stands on the desk beside Maker, on its side away from the model, within the brush's
        // reach; it follows Maker round the model (a hop and a puff).
        private void PlacePot(WindupCharacter maker)
        {
            if (maker == null) return;
            BuildPot();
            var room = arrival.handoff.meeting.environment;
            Vector3 at = maker.transform.position;
            Vector3 toward = Vector3.ProjectOnPlane(sculptSpot - at, Vector3.up);
            toward = toward.sqrMagnitude > 1e-6f ? toward.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, toward);
            float modelRadius = view != null ? Mathf.Max(view.GetComponent<Renderer>().bounds.extents.x, view.GetComponent<Renderer>().bounds.extents.z) : .04f;
            Vector3 best = at + right * .056f + toward * .012f;
            foreach (float side in new[] { 1f, -1f })
            {
                Vector3 p = at + right * side * .056f + toward * .012f;
                if (Vector3.ProjectOnPlane(p - sculptSpot, Vector3.up).magnitude < modelRadius + .02f) continue;
                if (!MakerStandClear(p)) continue;
                if (!Physics.Raycast(p + Vector3.up * .05f, Vector3.down, out var hit, .1f, room.GeometryMask, QueryTriggerInteraction.Ignore) || Mathf.Abs(hit.point.y - at.y) > .02f) continue;
                best = new Vector3(p.x, hit.point.y, p.z);
                break;
            }
            bool moved = !pot.gameObject.activeSelf || Vector3.Distance(pot.position, best) > .01f;
            pot.gameObject.SetActive(true);
            pot.position = best;
            if (moved) { ToyFx.Puff(best + Vector3.up * .01f, .005f, 2); ToySounds.Play(ToySounds.Cue.Bonk, best, .2f, 1.9f); }
        }

        private Mesh potPaintMesh;
        private void BuildPot()
        {
            if (pot != null) return;
            pot = new GameObject("Paint Pot").transform;
            pot.SetParent(transform, false);
            // Discs extrude along +z: turned upright, they stand on the desk.
            var tin = ToyMeshes.Disc(.0135f, .02f, new Color(.72f, .75f, .8f), new Color(.6f, .63f, .68f), "PaintPot");
            owned.Add(tin);
            var t = Part("Tin", tin);
            t.SetParent(pot, false);
            t.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var rim = ToyMeshes.Disc(.0145f, .0025f, new Color(.62f, .65f, .7f), new Color(.5f, .53f, .58f), "PaintPotRim");
            owned.Add(rim);
            var r = Part("Rim", rim);
            r.SetParent(pot, false);
            r.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            r.localPosition = new Vector3(0f, .0185f, 0f);
            potPaintMesh = ToyMeshes.Disc(.0122f, .0012f, Color.white, Color.white, "PaintPotPaint");
            owned.Add(potPaintMesh);
            potPaint = Part("Paint", potPaintMesh);
            potPaint.SetParent(pot, false);
            potPaint.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            potPaint.localPosition = new Vector3(0f, .0192f, 0f);
            pot.gameObject.SetActive(false);
        }

        private void SetPotColour(Color colour)
        {
            if (potPaintMesh != null) Paint(potPaintMesh, colour);
        }

        private void HidePot()
        {
            if (pot != null && pot.gameObject.activeSelf) { ToyFx.Puff(pot.position + Vector3.up * .01f, .005f, 2); pot.gameObject.SetActive(false); }
        }

        // A paint colour close to the bare wood would not show as it goes on: it is taken darker (or, if dark
        // already, lighter) until it stands out. The real texture replaces it at the reveal anyway.
        private static Color AgainstWood(Color c)
        {
            float Gap(Color a) => Mathf.Abs(a.r - WoodColour.r) + Mathf.Abs(a.g - WoodColour.g) + Mathf.Abs(a.b - WoodColour.b);
            float luma = c.r * .3f + c.g * .59f + c.b * .11f;
            for (int i = 0; i < 6 && Gap(c) < .45f; i++) c = luma > .35f ? Color.Lerp(c, Color.black, .2f) : Color.Lerp(c, Color.white, .2f);
            c.a = 1f;
            return c;
        }

        // The colours the person gave (palette) and the chosen picture's main colours.
        private Color[] PaintColours()
        {
            var list = new List<Color>();
            foreach (var s in InputSwatches) if (s.kind == "colour") list.Add(Hex(s.hex));
            if (LookJpeg != null)
            {
                var tex = new Texture2D(2, 2);
                try
                {
                    if (tex.LoadImage(LookJpeg))
                    {
                        // The object, not the plain light background of the product photo.
                        var picked = new List<Color>();
                        for (int y = 2; y < 14; y++)
                        for (int x = 2; x < 14; x++)
                        {
                            Color c = tex.GetPixelBilinear((x + .5f) / 16f, (y + .5f) / 16f);
                            Color.RGBToHSV(c, out _, out float s, out float v);
                            if (s < .12f && v > .7f) continue;
                            picked.Add(c);
                        }
                        foreach (var group in picked.GroupBy(c => { Color.RGBToHSV(c, out float h, out float s, out float v); return Mathf.RoundToInt(h * 6f) % 6 * 3 + Mathf.Clamp(Mathf.RoundToInt(v * 2f), 0, 2); })
                                                    .OrderByDescending(g => g.Count()).Take(3))
                        {
                            Color sum = Color.black;
                            foreach (var c in group) sum += c;
                            list.Add(sum / group.Count());
                        }
                    }
                }
                finally { Destroy(tex); }
            }
            if (list.Count == 0) list.Add(new Color(.9f, .6f, .35f));
            return list.Take(4).ToArray();
        }

        // ------------------------------------------------------------ 4. reveal

        // The painted model: exactly what the made thing will be (same mesh, material and place).
        private IEnumerator RevealModel(CoasterDesign design, bool painted)
        {
            Sculpt = SculptStage.Reveal;
            for (float until = Time.time + .8f; handBusy && Time.time < until; ) yield return null;
            var maker = cast.TryGetValue(Crew.Maker, out var m) ? m : null;
            var body = WindupBody.Of(maker);
            ReleaseMitts(body);
            if (makerBrush != null) makerBrush.gameObject.SetActive(false);
            HidePot();
            if (body != null) { body.StopWork(true); body.LimitPosture(SculptLean, SculptHeadDown); } else Cue(Crew.Maker, "encourage");
            yield return new WaitForSeconds(.3f);
            var mesh = CoasterMesh.Build(design);
            Paint(mesh, design.Tint(new Color(.95f, .85f, .62f)));
            if (viewMesh != null) { owned.Remove(viewMesh); Destroy(viewMesh); }
            viewMesh = mesh;
            owned.Add(viewMesh);
            if (view == null) { view = Part("Sculpt View", viewMesh); view.SetParent(transform, false); view.SetPositionAndRotation(sculptSpot, sculptTurn); }
            view.GetComponent<MeshFilter>().sharedMesh = viewMesh;
            view.GetComponent<MeshRenderer>().sharedMaterial = MadeMaterial(design);
            view.gameObject.SetActive(true);
            ClearPaintMarks();
            if (block != null) block.gameObject.SetActive(false);
            float h = viewMesh.bounds.size.y;
            ToyFx.Confetti(sculptSpot + Vector3.up * (h * .6f + .02f), painted ? 18 : 8);
            if (MadeCount == 0) Celebrate(sculptSpot + Vector3.up * h, .22f, 35); // F82: the first make gets a small shower
            ToyFx.Puff(sculptSpot + Vector3.up * h * .5f, .016f, 5);
            ToySounds.Play(ToySounds.Cue.Chime, sculptSpot, .8f, 1.15f);
            view.localScale = Vector3.one * 1.06f;
            StartCoroutine(ScaleTo(view, Vector3.one, .25f));
            Body(Crew.Maker)?.PlaySoon("React_Nod", .15f); // a pleased nod beside the work (a cheer jumps into it)
            StartCoroutine(After(.45f, () => Body(Crew.Spark)?.PlaySoon("Cheer", .2f)));
            yield return new WaitForSeconds(.6f);
        }

        // The made thing now stands where the view was (Build): the view goes, Maker goes back to its place.
        private void SculptHandOff()
        {
            if (view != null) view.gameObject.SetActive(false);
            // The made thing hidden while it was remade: kept as an earlier version beside it, it shows again
            // (replaced in place, it is already on its way out).
            if (hiddenProduct != null && Versions.Any(v => v.root == hiddenProduct)) hiddenProduct.gameObject.SetActive(true);
            hiddenProduct = null;
            versionCleared = null; // F81: the earlier version stays in the notebook
            EndSculpt(false);
        }

        // Shape never came (or the person moved on): the block crumbles away; the old made thing comes back.
        private void SculptFail()
        {
            if (block != null && block.gameObject.activeSelf)
            {
                ToyFx.Puff(block.position + Vector3.up * .01f, .012f, 6);
                StartCoroutine(ScaleTo(block, Vector3.zero, .3f));
            }
            if (view != null) view.gameObject.SetActive(false);
            ClearPaintMarks();
            if (hiddenProduct != null) { hiddenProduct.gameObject.SetActive(true); hiddenProduct = null; }
            BringClearedVersionBack(); // F81
            EndSculpt(true);
        }

        private void ClearPaintMarks()
        {
            foreach (var d in dots) if (d != null) Destroy(d.gameObject);
            dots.Clear();
            foreach (var m in bandMeshes) if (m != null) Destroy(m);
            bandMeshes.Clear();
        }

        private void EndSculpt(bool failed)
        {
            if (Sculpt == SculptStage.None) return;
            Sculpt = SculptStage.None;
            if (makerBrush != null) makerBrush.gameObject.SetActive(false);
            ShowTools(false);
            HidePot();
            foreach (var c in chips) if (c != null) Destroy(c.gameObject);
            chips.Clear();
            if (cast.TryGetValue(Crew.Maker, out var maker) && maker != null)
            {
                var body = WindupBody.Of(maker);
                ReleaseMitts(body);
                if (body != null) { if (failed) body.StopWork(false); body.ClearPosture(); }
                StartCoroutine(BackToSeat(maker));
            }
        }

        private IEnumerator BackToSeat(WindupCharacter maker)
        {
            yield return new WaitForSeconds(1.4f);
            if (Sculpt != SculptStage.None || maker == null) yield break;
            var pet = maker.movement;
            if (Vector3.Distance(pet.transform.position, makerHome) < .05f || pet.IsHeld) yield break;
            pet.UnlockMeetingSeat();
            yield return null;
            if (pet.GoTo(ClearSeat(Crew.Maker, makerHome), 3f)) // F51: its seat may have filled up while it worked
                for (float until = Time.time + 8f; Time.time < until && pet.State != Pet.MotionState.Idle && !pet.IsHeld; ) yield return null;
            for (int i = 0; i < 30 && !pet.MeetingSeatLocked && !pet.IsHeld; i++) { pet.LockMeetingSeat(); yield return null; }
        }

        private void SculptLateFrame()
        {
            if (Sculpt == SculptStage.None) return;
            if (cast.TryGetValue(Crew.Maker, out var maker) && maker != null && sculptFocus != null) maker.lookTarget = sculptFocus;
            // (While carving, PlaceTools keeps the eyes on the chisel's edge.)
            if (Sculpt == SculptStage.Clay && block != null && sculptFocus != null && !handBusy) sculptFocus.position = block.position + Vector3.up * Mathf.Min(blockHeight * .5f, .035f);
        }

        // ------------------------------------------------------------ meshes and easing

        // The model at `widthMm` across its widest side, standing on y = 0, in one colour (wood since F35).
        private static Mesh Scaled(Mesh source, float widthMm, Color colour)
        {
            var b = source.bounds;
            float scale = widthMm * .001f / Mathf.Max(1e-5f, Mathf.Max(b.size.x, b.size.z));
            var centre = new Vector3(b.center.x, b.min.y, b.center.z);
            var src = source.vertices;
            var v = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++) v[i] = (src[i] - centre) * scale;
            var mesh = new Mesh { name = "Sculpt", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = v;
            mesh.triangles = source.triangles;
            mesh.colors = Enumerable.Repeat(colour, v.Length).ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static IEnumerator TurnTo(Transform target, Quaternion to, float seconds)
        {
            if (target == null) yield break;
            Quaternion from = target.rotation;
            for (float t = 0f; t < 1f && target != null; t += Time.deltaTime / seconds)
            {
                target.rotation = Quaternion.Slerp(from, to, Mathf.SmoothStep(0f, 1f, t));
                yield return null;
            }
            if (target != null) target.rotation = to;
        }

        private static IEnumerator ScaleTo(Transform target, Vector3 to, float seconds)
        {
            if (target == null) yield break;
            Vector3 from = target.localScale;
            for (float t = 0f; t < 1f && target != null; t += Time.deltaTime / seconds)
            {
                target.localScale = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t));
                yield return null;
            }
            if (target != null) target.localScale = to;
        }

        private static IEnumerator Squash(Transform target, Vector3 squashed, Vector3 rest, float seconds)
        {
            if (target == null) yield break;
            target.localScale = squashed;
            for (float t = 0f; t < 1f && target != null; t += Time.deltaTime / seconds)
            {
                // A soft spring back (a little overshoot).
                float k = 1f - Mathf.Exp(-t * 6f) * Mathf.Cos(t * 9f);
                target.localScale = Vector3.LerpUnclamped(squashed, rest, k);
                yield return null;
            }
            if (target != null) target.localScale = rest;
        }
    }
}
