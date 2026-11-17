using System.Linq;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F39 on v2 (2026-11-12): the draft's width, height and depth are blueprint dimension lines along its edges
    // facing the person, each with a round knob. Pinch a knob (near or by the hand's ray; the mouse in the
    // Editor) and pull: v2 stretches along that side right away and the pill counts the millimetres. Words
    // ("2 cm taller") and the two-hand tape held up to v2 set them too; "measure it" asks Fixer for all three.
    // They show while a hand is near v2, while a size changes, and while Maker reads the plan back.
    // A clip's three are its width along the edge, its drop and its reach over the desk.
    public sealed partial class MakerSession
    {
        public int KnobDrags { get; private set; }
        public int DraggingKnob => knobAxis;

        private readonly DimView[] draftDims = new DimView[3];
        private float dimsUntil;
        private int knobAxis = -1;
        private Hand knobHand;
        private bool knobMouse;
        private float knobStartLength;
        private Vector3 knobStartScale;
        private float knobTickMm;

        // The draft's local axis for a size (W, H, D): a clip's width runs along its edge (local z), its reach is x.
        private int LocalAxis(int size) => DraftIsClip ? new[] { 2, 1, 0 }[size] : size;
        private Vector3 AxisWorld(int la) => la == 0 ? draftMesh.right : la == 1 ? draftMesh.up : draftMesh.forward;

        // A size's line: along the draft's edge that faces the person best (W on top, H at the right, D at the bottom).
        private bool DraftLine(int size, out Vector3 a, out Vector3 b)
        {
            a = b = Vector3.zero;
            if (draftMesh == null) return false;
            var mesh = draftMesh.GetComponent<MeshFilter>().sharedMesh;
            if (mesh == null) return false;
            var bb = mesh.bounds;
            int la = LocalAxis(size);
            Vector3 centre = draftMesh.TransformPoint(bb.center);
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - centre, Vector3.up).normalized : Vector3.back;
            Vector3 right = Vector3.Cross(Vector3.up, -toUser);
            int o1 = (la + 1) % 3, o2 = (la + 2) % 3;
            float bestScore = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 p0 = bb.min, p1 = bb.min;
                p0[o1] = p1[o1] = (i & 1) == 0 ? bb.min[o1] : bb.max[o1];
                p0[o2] = p1[o2] = (i & 2) == 0 ? bb.min[o2] : bb.max[o2];
                p0[la] = bb.min[la];
                p1[la] = bb.max[la];
                Vector3 w0 = draftMesh.TransformPoint(p0), w1 = draftMesh.TransformPoint(p1), mid = (w0 + w1) * .5f - centre;
                float score = size == 1 ? Vector3.Dot(mid, toUser) + Vector3.Dot(mid, right)
                            : size == 0 ? 2f * mid.y + Vector3.Dot(mid, toUser)
                            : -2f * mid.y + Vector3.Dot(mid, right);
                if (score <= bestScore) continue;
                bestScore = score;
                Vector3 outward = Vector3.ProjectOnPlane(mid, w1 - w0);
                outward = outward.sqrMagnitude > 1e-8f ? outward.normalized : Vector3.up;
                a = w0 + outward * .014f;
                b = w1 + outward * .014f;
            }
            // The knob at the top (H), the person's right (W), the far end (D).
            Vector3 d = b - a;
            bool flip = size == 1 ? d.y < 0f : size == 0 ? Vector3.Dot(d, right) < 0f : Vector3.Dot(d, toUser) > 0f;
            if (flip) { var t = a; a = b; b = t; }
            return true;
        }

        private void DraftDimsFrame()
        {
            bool show = Draft != null && draftMesh != null && Draft.gameObject.activeInHierarchy && (Confirming || Time.time < dimsUntil || knobAxis >= 0 || HandNearDraft(.16f) || EditorPreview);
            for (int i = 0; i < 3; i++)
            {
                if (!show) { ShowDim(draftDims[i], false); continue; }
                draftDims[i] ??= BuildDim("Draft " + "WHD"[i], true);
                if (!DraftLine(i, out var a, out var b)) { ShowDim(draftDims[i], false); continue; }
                ShowDim(draftDims[i], true);
                string[] names = DraftIsClip ? new[] { "W", "Drop", "Reach" } : new[] { "W", "H", "D" };
                DrawDim(draftDims[i], a, b, names[DraftSide(i)] + "  " + DraftSizeMm[i].ToString("0") + " mm", 2); // F88b: named as the person sees it
            }
        }

        private void HideDraftDims()
        {
            foreach (var v in draftDims) ShowDim(v, false);
            knobAxis = -1;
            knobHand = null;
            knobMouse = false;
        }

        private bool EditorPreview
        {
            get
            {
#if UNITY_EDITOR
                return !UnityEngine.XR.XRSettings.isDeviceActive;
#else
                return false;
#endif
            }
        }

        private bool HandNearDraft(float reach)
        {
            if (draftMesh == null) return false;
            var b = draftMesh.GetComponent<MeshRenderer>().bounds;
            b.Expand(reach * 2f);
            foreach (var hand in HandSet.Both())
                if (hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand) && b.Contains(PinchPoint(hand))) return true;
            return false;
        }

        private bool NearDraft(Vector3 p, float reach)
        {
            if (draftMesh == null) return false;
            var b = draftMesh.GetComponent<MeshRenderer>().bounds;
            b.Expand(reach * 2f);
            return b.Contains(p);
        }

        // ------------------------------------------------------------ the knobs

        private int KnobNear(Vector3 point, Ray? ray)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                var v = draftDims[i];
                if (v?.knob == null || !v.root.gameObject.activeInHierarchy || !v.knob.gameObject.activeSelf) continue;
                float d = Vector3.Distance(point, v.knob.position);
                if (d < .025f && d < bestD) { bestD = d; best = i; }
                if (ray.HasValue)
                {
                    float r = RayDistance(ray.Value, v.knob.position);
                    if (r < .018f && r + .5f < bestD) { bestD = r + .5f; best = i; }
                }
            }
            return best;
        }

        private bool KnobPinch(Hand hand, Vector3 point, Ray? ray)
        {
            if (Draft == null) return false;
            int axis = KnobNear(point, ray);
            if (axis < 0) return false;
            StartKnob(axis);
            knobHand = hand;
            return true;
        }

        private void StartKnob(int axis)
        {
            knobAxis = axis;
            knobStartScale = DraftScale;
            knobStartLength = DraftLine(axis, out var a, out var b) ? Vector3.Distance(a, b) : 0f;
            knobTickMm = DraftSizeMm[axis];
            KnobDrags++;
            Heard();
            ToySounds.Play(ToySounds.Cue.Press, draftDims[axis].knob.position, .35f, 1.8f);
        }

        // The knob follows the hand along its line: the length is from the fixed end (H: the bottom) or twice the
        // distance from the middle (W and D grow both ways from the centre, as the twin scales about its middle).
        private void DragKnobTo(Vector3 point)
        {
            if (knobAxis < 0 || draftMesh == null || knobStartLength <= 1e-4f) return;
            if (!DraftLine(knobAxis, out var a, out var b)) return;
            Vector3 dir = (b - a).normalized;
            float length = knobAxis == 1 ? Vector3.Dot(point - a, dir) : 2f * Mathf.Abs(Vector3.Dot(point - (a + b) * .5f, dir));
            float lineNow = Vector3.Distance(a, b);
            // The line's length now vs at the start gives the scale; snapped to whole millimetres.
            var scale = DraftScale;
            float wanted = Mathf.Max(.005f, length) / Mathf.Max(1e-4f, lineNow) * scale[knobAxis];
            float mm = Mathf.Round(V1Size()[knobAxis] * wanted);
            scale[knobAxis] = mm / Mathf.Max(1f, V1Size()[knobAxis]);
            SetDraftScale(scale);
            if (Mathf.Abs(DraftSizeMm[knobAxis] - knobTickMm) >= 5f)
            {
                knobTickMm = DraftSizeMm[knobAxis];
                ToySounds.Play(ToySounds.Cue.Press, point, .08f, 2.4f);
            }
        }

        private void EndKnob()
        {
            if (knobAxis < 0) return;
            int axis = knobAxis;
            knobAxis = -1;
            knobHand = null;
            knobMouse = false;
            if ((DraftScale - knobStartScale).magnitude > .004f) AnnounceDraftSize(axis);
        }

        // A point on the knob's line nearest a ray (the hand's ray from afar, the mouse).
        private Vector3 OnKnobLine(Ray ray)
        {
            if (knobAxis < 0 || !DraftLine(knobAxis, out var a, out var b)) return ray.origin;
            Vector3 u = (b - a).normalized, v = ray.direction, w = a - ray.origin;
            float uv = Vector3.Dot(u, v), denom = 1f - uv * uv;
            if (denom < 1e-6f) return a;
            float s = (uv * Vector3.Dot(v, w) - Vector3.Dot(u, w)) / denom;
            return a + u * s;
        }

        private void KnobFrame(Hand hand, bool pinching, Vector3 point)
        {
            if (!pinching) { EndKnob(); return; }
            if (hand != null && HandIO.Pointer(hand, out Pose pointer) && Vector3.Distance(point, draftDims[knobAxis].knob.position) > .08f)
                DragKnobTo(OnKnobLine(new Ray(pointer.position, pointer.forward)));
            else DragKnobTo(point);
        }

        // "Measure it": Fixer gives all three, and the lines show for a while.
        private void MeasureDraft()
        {
            dimsUntil = Time.time + 12f;
            var s = DraftSizeMm;
            Body(Crew.Fixer)?.Play("Decision_Present");
            Body(Crew.Fixer)?.LookAtPoint(DraftCentre(), 3f);
            Say(Crew.Fixer, DraftIsClip ? $"{s.x:0} wide, {s.y:0} drop, {s.z:0} reach, in millimetres. Pull a knob to change one."
                                        : $"{s.x:0} wide, {s.y:0} tall, {s.z:0} deep, in millimetres. Pull a knob to change one.");
        }

        // The two-hand tape held up to v2 sets that side (the tape's direction picks which).
        private bool DraftTapeSize(float mm)
        {
            // Both ends at v2 (5 cm), and the tape nearer v2 than anything else made (measuring v1 beside it is not v2).
            if (Draft == null || draftMesh == null || !NearDraft(Tape.A, .05f) || !NearDraft(Tape.B, .05f)) return false;
            Vector3 mid = (Tape.A + Tape.B) * .5f;
            float toDraft = (draftMesh.GetComponent<MeshRenderer>().bounds.ClosestPoint(mid) - mid).magnitude;
            foreach (var made in MadeThings())
            {
                var r = made.GetComponentInChildren<MeshRenderer>();
                if (r != null && (r.bounds.ClosestPoint(mid) - mid).magnitude < toDraft) return false;
            }
            Vector3 d = (Tape.B - Tape.A).normalized;
            int la = 0;
            float best = -1f;
            for (int i = 0; i < 3; i++) { float c = Mathf.Abs(Vector3.Dot(d, AxisWorld(i))); if (c > best) { best = c; la = i; } }
            int size = Enumerable.Range(0, 3).First(s => LocalAxis(s) == la);
            var scale = DraftScale;
            scale[size] = mm / Mathf.Max(1f, V1Size()[size]);
            SetDraftScale(scale);
            AnnounceDraftSize(size);
            return true;
        }

        public void SimulateKnob(int axis, float mm)
        {
            if (Draft == null) return;
            dimsUntil = Time.time + 10f;
            DraftDimsFrame();
            StartKnob(axis);
            var scale = DraftScale;
            scale[axis] = mm / Mathf.Max(1f, V1Size()[axis]);
            // Drag through the line in a few steps, as a hand would.
            if (DraftLine(axis, out var a, out var b))
            {
                Vector3 dir = (b - a).normalized;
                float length = Vector3.Distance(a, b) * scale[axis] / Mathf.Max(1e-4f, DraftScale[axis]);
                Vector3 target = axis == 1 ? a + dir * length : (a + b) * .5f + dir * length * .5f;
                for (int i = 1; i <= 6; i++) { DragKnobTo(Vector3.Lerp(b, target, i / 6f)); draftMesh.localScale = DraftIsClip ? Vector3.one : DraftScale; }
            }
            EndKnob();
        }

#if UNITY_EDITOR
        private bool EditorKnob(UnityEngine.InputSystem.Mouse mouse, Ray ray, Camera camera)
        {
            if (knobMouse && knobAxis >= 0)
            {
                if (mouse.leftButton.isPressed) DragKnobTo(OnKnobLine(ray));
                else EndKnob();
                return true;
            }
            if (!mouse.leftButton.wasPressedThisFrame || Draft == null) return false;
            int axis = KnobNear(Vector3.positiveInfinity, ray);
            if (axis < 0) return false;
            StartKnob(axis);
            knobMouse = true;
            return true;
        }
#endif
    }
}
