using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Oculus.Interaction.Input;
using UnityEngine;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // The brush (2026-11-11, after the Quest try). With the palette up, dip any fingertip of the other hand
    // into a colour or a texture: that finger is now the brush - its tip carries the paint (a glossy cap of
    // the colour, or of the material). Rub something made with that fingertip and the part you rub takes the
    // paint, a few strokes at a time (MakerSession.Strokes.cs) - a coaster's or a cup's rim, floor, bottom,
    // outside or inside. Dip again to change paint. Paint on made things is remembered with them (and comes
    // back with them).
    // The 8 cm paint test cube is no longer put in the room (2026-11-12: it read as a placeholder); only the
    // verify scripts place it (SimulateTestCube) to test painting single faces.
    // The eraser (2026-11-11, it used to lie in the palette's middle): the moment a finger takes paint, an
    // eraser sits on the thumb of the same hand - on the index when the thumb took the paint. It rubs paint off
    // the same way the brush puts it on (MakerSession.Strokes.cs). The paint and the eraser both go when
    // nothing is painted or erased for ToolIdle seconds (F49: no longer when the palette is put away).
    public sealed partial class MakerSession
    {
        public Swatch BrushSwatch => brushSwatch;
        public HandJointId BrushFinger => brushFinger;
        public HandJointId EraserFinger => eraserFinger;
        public int PaintStrokes { get; private set; }
        public Transform TestCube { get; private set; }
        public Transform BrushView => brush;
        public Transform EraserView => eraser;
        public const float ToolIdle = 5f, FinishToolIdle = 14f, DipReach = .022f, TouchReach = .018f, CubeSize = .08f;

        // In the order a dip looks at them: the thumb last, so a thumb brushing past the paint while another
        // finger dips does not become the brush.
        private static readonly (HandJointId tip, HandJointId last)[] Fingers =
        {
            (HandJointId.HandIndexTip, HandJointId.HandIndex3), (HandJointId.HandMiddleTip, HandJointId.HandMiddle3),
            (HandJointId.HandRingTip, HandJointId.HandRing3), (HandJointId.HandPinkyTip, HandJointId.HandPinky3),
            (HandJointId.HandThumbTip, HandJointId.HandThumb3)
        };

        private Swatch brushSwatch;
        private Hand brushHand;
        private HandJointId brushFinger = HandJointId.HandIndexTip, eraserFinger = HandJointId.HandThumbTip;
        private Transform brush, eraser;
        private float brushUsedAt, nextPaintAt, dragStartAt;
        private bool eraserHinted;
        private Vector3 dragStartPoint;
        private Vector3? brushTipOverride, eraserTipOverride; // tests: where the tips are

        // ------------------------------------------------------------ dipping a finger

        // (A pinch on a swatch drags it, as before; the brush is loaded by dipping a fingertip.)
        private bool SwatchTapped(Swatch swatch, Hand hand, Vector3 at) => false;

        // With the palette up: a fingertip of the other hand touching a swatch takes its paint.
        private void UpdateDip()
        {
            if (FormFirst && Begun && !ShapeConfirmed && TestCube == null) return;
            if (!PaletteOnPalm || paletteBoard == null || !paletteBoard.gameObject.activeInHierarchy || swatchViews.Count == 0) { ForgetTipHold(); return; }
            if (Jiggling) { ForgetTipHold(); return; } // F103: fingers pressing the x's take no paint
            var hand = LiveHand(Handedness.Right);
            if (hand == null) { ForgetTipHold(); return; }
            foreach (var (tip, last) in Fingers)
                if (HandIO.Joint(hand, tip, out Pose end) && DipAt(hand, tip, end.position))
                {
                    // F103: the index tip resting there: a long press starts deleting paints.
                    if (tip == HandJointId.HandIndexTip && !HandIO.RawPinching(hand) && dipSlot >= 0) SwatchTipHold(hand, dipSlot, end.position);
                    return;
                }
            ForgetTipHold();
        }

        private int dipSlot = -1; // (the paint the last DipAt was in)

        // A fingertip at `point`: in a swatch, the finger takes its paint. The finger wearing the eraser takes
        // none (it stays the eraser until the paint and the eraser are put away).
        private bool DipAt(Hand hand, HandJointId tip, Vector3 point)
        {
            dipSlot = -1;
            if (eraser != null && tip == eraserFinger) return false;
            var shown = Palette.Skip(Mathf.Max(0, Palette.Count - PaletteSlots)).ToList();
            for (int i = 0; i < swatchViews.Count && i < shown.Count; i++)
            {
                if (swatchViews[i] == null || Vector3.Distance(point, swatchViews[i].position) > DipReach) continue;
                dipSlot = i;
                if (brushSwatch == shown[i] && brushHand == hand && brushFinger == tip) { brushUsedAt = Time.time; return true; }
                LoadBrush(shown[i], hand, tip);
                return true;
            }
            return false;
        }

        public void LoadBrush(Swatch swatch, Hand hand, HandJointId finger = HandJointId.HandIndexTip)
        {
            if (FormFirst && Begun && !ShapeConfirmed && TestCube == null) return;
            if (WheelOpen) { WriteInputLog("colour wheel: a paint was picked on the palette"); CloseWheel(); } // F95
            DropPaint();
            brushSwatch = swatch;
            brushHand = hand;
            brushFinger = finger;
            paintTool.Reset(); // a new paint starts a new stroke (the last one's part is not locked any more)
            brushUsedAt = Time.time;
            brush = new GameObject("Brush").transform;
            brush.SetParent(transform, false);
            // The paint on the fingertip: a glossy cap of the colour (or the material), a drop hanging off it.
            var tile = swatch.kind == "texture" && swatch.image != null && File.Exists(swatch.image) ? LoadSwatchTexture(swatch.image) : null;
            Transform cap = tile != null ? MaterialBall(tile, .005f) : ColourBall("Paint Cap", Hex(swatch.hex), .005f);
            cap.name = "Paint";
            cap.SetParent(brush, false);
            cap.localPosition = new Vector3(0f, 0f, -.004f);
            cap.localRotation = Quaternion.identity;
            cap.localScale = new Vector3(.019f, .017f, .024f);
            Transform drop = tile != null ? MaterialBall(tile, .003f) : ColourBall("Paint Drop", Hex(swatch.hex), .003f);
            drop.name = "Drop";
            drop.SetParent(brush, false);
            drop.localPosition = new Vector3(0f, -.008f, .002f);
            drop.localScale = Vector3.one * .006f;
            // The eraser comes with the paint: on the thumb, or on the index when the thumb took the paint.
            PutEraserOn(finger == HandJointId.HandThumbTip ? HandJointId.HandIndexTip : HandJointId.HandThumbTip);
            ToySounds.Play(ToySounds.Cue.Pop, paletteBoard != null ? paletteBoard.position : Vector3.zero, .5f, 1.6f);
            Heard();
            Talk.Add("Person dipped a finger in " + swatch.name);
            if (PaintStrokes == 0 && MadeThings().Count > 0) Caption(Crew.Spark, "Swipe that finger over what we made!", 3f);
        }

        // The eraser on `finger` (kept as it is when it is already there).
        private void PutEraserOn(HandJointId finger)
        {
            if (eraser != null && eraserFinger == finger) return;
            if (eraser != null) { ToyFx.Puff(eraser.position, .005f, 2); Destroy(eraser.gameObject); }
            eraserFinger = finger;
            eraseTool.Reset();
            eraser = EraserTip();
            eraser.gameObject.SetActive(false); // shown once it is on its fingertip
            eraser.localScale = Vector3.one * .2f;
            StartCoroutine(Grow(eraser, 1f, .25f));
            ToySounds.Play(ToySounds.Cue.Press, paletteBoard != null ? paletteBoard.position : Vector3.zero, .3f, 1.5f);
        }

        // Puts the paint and the eraser away.
        public void PutBrushDown()
        {
            DropPaint();
            if (eraser != null) { ToyFx.Puff(eraser.position, .006f, 2); Destroy(eraser.gameObject); }
            eraser = null;
            eraserTipOverride = null;
            eraseTool.Reset();
        }

        private void DropPaint()
        {
            if (brush != null) { ToyFx.Puff(brush.position, .006f, 2); Destroy(brush.gameObject); }
            brush = null;
            brushSwatch = null;
            brushHand = null;
            brushTipOverride = null;
        }

        private void Tint(MeshRenderer renderer, Color colour)
        {
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            tint ??= new MaterialPropertyBlock();
            tint.Clear();
            tint.SetColor("_BaseColor", colour);
            tint.SetFloat("_VertexColor", 0f);
            renderer.SetPropertyBlock(tint);
        }

        // ------------------------------------------------------------ painting

        private void UpdateBrush()
        {
            UpdateDip();
            if (brushSwatch == null || brush == null) { if (eraser != null) PutBrushDown(); return; }
            // The paint and the eraser go together when nothing was painted or erased for ToolIdle seconds (longer
            // during the finish lesson). F49: putting the palette away no longer takes them: the left hand comes
            // down while the right one paints the made thing.
            if (Time.time - brushUsedAt > (Current == Step.Finish ? FinishToolIdle : ToolIdle)) { PutBrushDown(); return; }
            // F70: the brush and the eraser are checked in the same tick, the brush first (since one touch floods a
            // whole part, an eraser tick landing before the brush's would wipe the part the brush was on).
            bool tick = Time.time >= nextPaintAt;
            if (tick) nextPaintAt = Time.time + .033f;
            if (Follow(brush, brushFinger, brushTipOverride, out Vector3 tip, out Vector3 along) && tick)
            {
                // The fingertip and just past it (tracked tips sit a little inside the real finger's end).
                if (!Touch(paintTool, tip) && !brushTipOverride.HasValue) Touch(paintTool, tip + along * .008f);
            }
            // The eraser rubs only while the brush is on nothing (when both touch, the brush wins) and not while
            // the hand pinches (grabbing something between thumb and finger is not erasing).
            if (eraser != null && Follow(eraser, eraserFinger, eraserTipOverride, out Vector3 rub, out Vector3 rubAlong) && tick)
            {
                bool painting = Time.time - paintTool.at < .3f;
                if (!painting && !Pinching(brushHand) && !Touch(eraseTool, rub) && !eraserTipOverride.HasValue) Touch(eraseTool, rub + rubAlong * .008f);
            }
        }

        // Puts `view` on the fingertip (or the test's tip); false when the finger is not tracked.
        private bool Follow(Transform view, HandJointId finger, Vector3? over, out Vector3 tip, out Vector3 along)
        {
            if (over.HasValue)
            {
                tip = over.Value;
                along = Head != null ? (tip - Head.position).normalized : Vector3.forward;
            }
            else if (brushHand != null && brushHand.isActiveAndEnabled && HandIO.Connected(brushHand) &&
                     HandIO.Joint(brushHand, finger, out Pose end) && HandIO.Joint(brushHand, LastJoint(finger), out Pose last))
            {
                along = (end.position - last.position).normalized;
                tip = end.position;
            }
            else
            {
                tip = along = Vector3.zero;
                view.gameObject.SetActive(false);
                return false;
            }
            if (along.sqrMagnitude < 1e-6f) along = Vector3.forward;
            view.gameObject.SetActive(true);
            view.SetPositionAndRotation(tip, Quaternion.LookRotation(along, Vector3.up));
            return true;
        }

        // (F49: the middle-finger pinch is read from the real hand only when no synthetic hand stands in: with
        // HandIO's test hands the Editor's untracked hand could report it and stop the eraser.)
        private static bool Pinching(Hand hand) => hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand) &&
            (HandIO.Pinching(hand) || (!HandIO.Active && hand.GetFingerIsPinching(HandFinger.Middle)));

        private static HandJointId LastJoint(HandJointId tip)
        {
            foreach (var (t, l) in Fingers) if (t == tip) return l;
            return HandJointId.HandIndex3;
        }

        // The part under the brush (or eraser) tip, within 1.8 cm of its surface, is brushed there. With things
        // side by side (the test cube next to a made thing), the one nearest the fingertip is brushed.
        private bool Touch(Tool tool, Vector3 point)
        {
            if (brushSwatch == null) return false;
            tool.tip = point;
            PaintableThing best = null;
            int bestPart = -1;
            Vector3 bestSurface = point, bestNormal = Vector3.up;
            float bestDistance = TouchReach;
            foreach (var thing in PaintableThing.All)
            {
                if (thing == null || thing.Renderer == null) continue;
                var bounds = thing.Renderer.bounds;
                bounds.Expand(.03f);
                if (!bounds.Contains(point)) continue;
                int part = thing.Closest(point, out Vector3 surface, out Vector3 normal, out float distance);
                if (part < 0 || distance > bestDistance) continue;
                best = thing;
                bestPart = part;
                bestSurface = surface;
                bestNormal = normal;
                bestDistance = distance;
            }
            if (best == null) return false;
            // A stroke stays on the part it started on: sliding over an edge does not paint the next face.
            if (StrokeLocked(tool, best, bestPart)) return true;
            Stroke(tool, best, bestPart, bestSurface, bestNormal);
            return true;
        }

        private Material SwatchMaterial(string kind, string hex, string image)
        {
            var tile = kind == "texture" && image != null && File.Exists(image) ? LoadSwatchTexture(image) : null;
            // F57: on a made thing a texture lies in real size from three sides (no seams, no stretched patches).
            return tile != null ? TileMaterial(tile, Vector2.one, true) : ColourMaterial(Hex(hex));
        }

        private Transform MadeRootOf(Transform part) => MadeThings().FirstOrDefault(m => part.IsChildOf(m));

        // Paint marks back on a made thing (restored from memory).
        private void ApplyMarks(Transform made, List<PaintableThing.Mark> marks)
        {
            if (made == null || marks == null) return;
            var thing = made.GetComponentInChildren<PaintableThing>();
            if (thing == null) return;
            foreach (var mark in marks)
            {
                int part = thing.PartIndex(mark.part);
                if (part >= 0) thing.Apply(part, SwatchMaterial(mark.kind, mark.hex, mark.image), new PaintableThing.Mark { kind = mark.kind, hex = mark.hex, image = mark.image });
            }
        }

        private void RememberPaint(Transform made)
        {
            if (made == null || made == TestCube) return;
            if (made == Product) FinishActivity();
            else if (notebookOwners.TryGetValue(made, out var id))
            {
                var painted = NotebookWorks.FirstOrDefault(w => w.id == id && w.completed);
                if (painted != null) ReopenWork(painted, made);
            }
            QueueNotebookSnapshot(made);
            if (made == Product) { Remember(); return; }
            if (!madeNames.TryGetValue(made, out var name)) return;
            var memory = LoadMemory();
            var item = SavedItem(memory, made, name);
            if (item == null) return;
            item.paint = made.GetComponentInChildren<PaintableThing>()?.SavedMarks();
            try { File.WriteAllText(MemoryPath, JsonConvert.SerializeObject(memory, Formatting.Indented)); }
            catch (System.Exception exception) { Debug.LogWarning("[Windups Maker] memory: " + exception.Message); }
        }

        // ------------------------------------------------------------ the paint test cube (tests only)

        // Verify scripts only: an 8 cm cube with six paintable faces on a free desk spot. The app never
        // places it.
        public Transform SimulateTestCube()
        {
            PlaceTestCube();
            return TestCube;
        }

        private void PlaceTestCube()
        {
            if (TestCube != null || Head == null || arrival == null) return;
            Vector3 spot = CubeSpot();
            var root = new GameObject("Paint Test Cube").transform;
            root.SetParent(transform, false);
            var go = new GameObject("Mesh", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(root, false);
            go.transform.localScale = Vector3.one * CubeSize;
            go.transform.localPosition = Vector3.up * CubeSize * .5f;
            var mesh = PaintableThing.CubeMesh();
            owned.Add(mesh);
            var filter = go.GetComponent<MeshFilter>();
            var renderer = go.GetComponent<MeshRenderer>();
            filter.sharedMesh = mesh;
            renderer.sharedMaterial = ColourMaterial(new Color(.94f, .92f, .88f));
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            PaintableThing.Make(filter, renderer, mesh, PaintableThing.CubePart, PaintableThing.CubeParts);
            go.AddComponent<BoxCollider>();
            var body = root.gameObject.AddComponent<Rigidbody>();
            body.mass = .1f;
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            Vector3 toUser = Vector3.ProjectOnPlane(Head.position - spot, Vector3.up);
            root.SetPositionAndRotation(spot, Quaternion.LookRotation(toUser.sqrMagnitude > 1e-4f ? -toUser.normalized : Vector3.forward, Vector3.up) * Quaternion.Euler(0f, 30f, 0f));
            TestCube = root;
            ToyFx.Puff(spot + Vector3.up * .04f, .012f, 4);
        }

        // Something new is built right next to the paint test cube (within CubeClear): the cube hops to a free
        // spot, so it never stands in front of what the person wants to paint.
        public const float CubeClear = .12f;
        private void MoveCubeAside(Transform made)
        {
            if (TestCube == null || made == null || made == TestCube || held == TestCube || Head == null) return;
            var near = made.GetComponentInChildren<MeshRenderer>();
            var cube = TestCube.GetComponentInChildren<MeshRenderer>();
            if (near == null || cube == null) return;
            Bounds around = near.bounds;
            around.Expand(new Vector3(CubeClear * 2f, .2f, CubeClear * 2f));
            if (!around.Intersects(cube.bounds)) return;
            Vector3 spot = CubeSpot();
            if (Vector3.ProjectOnPlane(spot - made.position, Vector3.up).magnitude < CubeClear) return; // nowhere better
            var body = TestCube.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = true;
            ToyFx.Puff(TestCube.position + Vector3.up * .04f, .01f, 3);
            StartCoroutine(Slide(TestCube, spot));
            CubeMoves++;
        }
        public int CubeMoves { get; private set; }

        // A free spot on the crew's desk (else any desk-like surface in reach), ahead and to the left.
        private Vector3 CubeSpot()
        {
            Vector3 forward = Vector3.ProjectOnPlane(Head.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .01f) forward = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 aim = Head.position + forward * .4f - right * .2f;
            int mask = arrival.handoff.meeting.environment.GeometryMask;
            var busy = Occupied();
            foreach (var c in cast.Values) if (c != null) busy.Add((c.transform.position, .07f));
            if (arrival.MicShown) busy.Add((arrival.MicPosition - Vector3.up * WindupsArrival.MicBase, .06f));
            // The crew's desk when they are up on one, else a desk-high surface within reach.
            var feet = cast.Values.Where(c => c != null).Select(c => c.transform.position.y).OrderBy(y => y).ToList();
            float floor = arrival.handoff.meeting.environment.Room != null ? arrival.handoff.meeting.environment.Room.GetRoomBounds().min.y : Head.position.y - 1.6f;
            float crew = feet.Count > 0 && feet[feet.Count / 2] > floor + .2f ? feet[feet.Count / 2] : Head.position.y - .75f;
            float best = float.MaxValue;
            Vector3 spot = Vector3.zero;
            for (float ahead = .2f; ahead <= .8f; ahead += .05f)
                for (float side = -.6f; side <= .3f; side += .05f)
                {
                    Vector3 above = Head.position + forward * ahead + right * side;
                    if (!Physics.Raycast(new Ray(above, Vector3.down), out var hit, 2.2f, mask, QueryTriggerInteraction.Ignore) || hit.normal.y < .9f) continue;
                    if (hit.point.y > Head.position.y - .2f || hit.point.y < Head.position.y - 1.2f) continue;
                    if (busy.Any(b => Mathf.Abs(b.at.y - hit.point.y) < .25f && Vector3.ProjectOnPlane(b.at - hit.point, Vector3.up).magnitude < b.radius + .07f)) continue;
                    bool flat = true;
                    foreach (var o in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                        flat &= Physics.Raycast(hit.point + o * .05f + Vector3.up * .05f, Vector3.down, out var h, .1f, mask, QueryTriggerInteraction.Ignore) && Mathf.Abs(h.point.y - hit.point.y) < .01f;
                    if (!flat) continue;
                    float score = Vector3.ProjectOnPlane(hit.point - aim, Vector3.up).magnitude + Mathf.Abs(hit.point.y - crew) * .8f;
                    if (score < best) { best = score; spot = hit.point; }
                }
            return spot != Vector3.zero ? spot : DeskPointInFront() - right * .2f;
        }

        // ------------------------------------------------------------ tests

        public Swatch SimulateSwatch(string kind, Color colour, string name, byte[] picture = null) =>
            AddSwatch(kind, colour, name, kind == "texture" && picture != null ? MaterialTile.Jpeg(picture) ?? picture : picture);
        public Material SwatchLook(Swatch swatch) => SwatchMaterial(swatch.kind, swatch.hex, swatch.image);
        public bool SimulateDip(Vector3 fingertip, HandJointId finger = HandJointId.HandMiddleTip) => DipAt(null, finger, fingertip);
        public Vector3 SwatchSpot(int shownIndex) => shownIndex >= 0 && shownIndex < swatchViews.Count && swatchViews[shownIndex] != null ? swatchViews[shownIndex].position : Vector3.zero;
        public int QueuedLines => lines.Count;
        public void SimulateSay(string text) => Say(Crew.Maker, text);
        public void SimulateLoadBrush(int paletteIndex) { if (paletteIndex >= 0 && paletteIndex < Palette.Count) LoadBrush(Palette[paletteIndex], null); }
        public void SimulateBrushAt(Vector3 tip) { brushTipOverride = tip; }
        public void SimulateBrushLifted() { brushTipOverride = null; }
        public void SimulateEraserAt(Vector3 tip) { eraserTipOverride = tip; }
        public void SimulatePalmUp(bool up) { editorPalm = up; }
        public Material PartMaterial(Transform made, string part)
        {
            var thing = made != null ? made.GetComponentInChildren<PaintableThing>() : null;
            int i = thing != null ? thing.PartIndex(part) : -1;
            return i >= 0 ? thing.Renderer.sharedMaterials[i] : null;
        }
    }
}
