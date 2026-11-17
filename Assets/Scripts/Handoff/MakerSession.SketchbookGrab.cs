using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    public sealed partial class MakerSession
    {
        private const int SketchbookPointerId = 983613;
        private Grabbable sketchbookGrab;
        private GrabFreeTransformer sketchbookTransformer;
        private HandGrabInteractable sketchbookNear;
        private BoxCollider sketchbookLeftEdge;
        private bool takingSketchbook, paperInFlight, sketchbookDragging, sketchbookNearDrag;
        private Hand sketchbookHand;
        private float sketchbookDistance;
        private Vector3 sketchbookOffset;
        private Pose sketchbookPointer;
        private Plane sketchbookMousePlane;

        private bool SketchbookHeld => paper != null && paper.gameObject.activeInHierarchy &&
                                      sketchbookGrab != null && sketchbookGrab.SelectingPointsCount > 0;

        private void BuildSketchbookGrip()
        {
            // Same kinematic, no-throw ISDK grip as the notebook, sized to this square cover.
            paper = Instantiate(Resources.Load<GameObject>("MakerNotebookGrip"), transform).transform;
            paper.name = "Drawing Paper";
            sketchbookGrab = paper.GetComponent<Grabbable>();
            sketchbookTransformer = paper.gameObject.AddComponent<GrabFreeTransformer>();
            sketchbookGrab.InjectOptionalOneGrabTransformer(sketchbookTransformer);
            sketchbookGrab.InjectOptionalTwoGrabTransformer(sketchbookTransformer);
            sketchbookNear = paper.GetComponentInChildren<HandGrabInteractable>();
            sketchbookNear.InjectSupportedGrabTypes(GrabTypeFlags.Pinch);
            var edges = paper.GetComponents<BoxCollider>();
            sketchbookLeftEdge = edges[0];
            float half = (PaperSize + .01f) * .5f, band = .018f;
            for (int i = 0; i < edges.Length; i++)
            {
                float sign = i % 2 == 0 ? -1f : 1f;
                edges[i].center = i < 2 ? new Vector3(sign * (half - band * .5f), 0f, 0f)
                                       : new Vector3(0f, sign * (half - band * .5f), 0f);
                edges[i].size = i < 2 ? new Vector3(band, 2f * half, .018f)
                                     : new Vector3(2f * (half - band), band, .018f);
            }
        }

        private void SketchbookGrip(bool offered)
        {
            // The small offered book can be pinched anywhere. On the open page only the rim grabs,
            // leaving the centre, the undo button and the check mark free for drawing.
            const float band = .018f;
            float half = (PaperSize + .01f) * .5f;
            sketchbookLeftEdge.center = offered ? Vector3.zero : new Vector3(-half + band * .5f, 0f, 0f);
            sketchbookLeftEdge.size = new Vector3(offered ? 2f * half : band, 2f * half, offered ? .05f : .018f);
            // The SDK caches its scale constraints at initialization. Refresh them when the book changes size.
            sketchbookTransformer.Initialize(sketchbookGrab);
            // F60: the held-out book is taken only by a pinch on it, decided by the session (SketchbookPinch); the hand
            // SDK's near grab took it from hands working beside Maker. The open page's rim keeps the SDK grab (F53).
            sketchbookNear.enabled = !offered;
        }

        private bool SketchbookGripAt(Vector3 local, float depth)
        {
            float half = (PaperSize + .01f) * .5f;
            float edge = Mathf.Max(Mathf.Abs(local.x), Mathf.Abs(local.y));
            return Mathf.Abs(local.z) <= depth && edge <= half &&
                   (OfferingBook || edge >= half - .018f);
        }

        private bool SketchbookOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            return !paperInFlight && (OfferingBook || (Drawing && !drawingDone)) &&
                   paper != null && paper.gameObject.activeInHierarchy &&
                   new Plane(paper.forward, paper.position).Raycast(ray, out distance) &&
                   SketchbookGripAt(paper.InverseTransformPoint(ray.GetPoint(distance)), .01f);
        }

        private bool SketchbookPinch(Hand hand, Vector3 point, Ray? ray)
        {
            if (SketchbookHeld || takingSketchbook) return true;
            if (paperInFlight || !(OfferingBook || (Drawing && !drawingDone)) ||
                paper == null || !paper.gameObject.activeInHierarchy) return false;
            if (hand != null && !float.IsInfinity(point.x))
            {
                // F60: a pinch on the held-out book itself (within 1.5 cm of it) takes it; nearer things go first
                // (NearestPinch).
                if (OfferingBook)
                {
                    if (BookDistance(point, out _) > .015f) return false;
                    sketchbookHand = hand;
                    sketchbookDragging = true;
                    sketchbookNearDrag = true;
                    sketchbookOffset = paper.position - point;
                    sketchbookPointer = new Pose(point, HandTurn(hand));
                    SketchbookEvent(PointerEventType.Hover);
                    SketchbookEvent(PointerEventType.Select);
                    return true;
                }
                if (SketchbookGripAt(paper.InverseTransformPoint(point), .045f)) return true; // SDK near grip on the open page's rim
            }
            if (!ray.HasValue || !SketchbookOnRay(ray.Value, out float distance)) return false;
            Vector3 at = ray.Value.GetPoint(distance);
            if (hand != null && Vector3.Distance(point, at) <= RemoteGrab.NearLimit) return true;
            sketchbookHand = hand;
            sketchbookDragging = true;
            sketchbookDistance = distance;
            sketchbookPointer = new Pose(at, HandTurn(hand));
            sketchbookMousePlane = new Plane(-ray.Value.direction, at);
            SketchbookEvent(PointerEventType.Hover);
            SketchbookEvent(PointerEventType.Select);
            return true;
        }

        private void SketchbookFrame()
        {
            if (sketchbookDragging)
            {
                if (sketchbookHand != null)
                {
                    if (!sketchbookHand.isActiveAndEnabled || !HandIO.Connected(sketchbookHand) || !HandIO.Pinching(sketchbookHand))
                        EndSketchbookGrab();
                    else if (sketchbookNearDrag)
                        sketchbookPointer = new Pose(PinchPoint(sketchbookHand), HandTurn(sketchbookHand));
                    else if (HandIO.Pointer(sketchbookHand, out Pose pointer))
                        sketchbookPointer = new Pose(pointer.position + pointer.forward * sketchbookDistance, HandTurn(sketchbookHand));
                }
#if UNITY_EDITOR
                else
                {
                    var mouse = UnityEngine.InputSystem.Mouse.current;
                    var camera = Camera.main;
                    if (mouse == null || camera == null || !mouse.leftButton.isPressed) EndSketchbookGrab();
                    else
                    {
                        var ray = camera.ScreenPointToRay(mouse.position.ReadValue());
                        if (sketchbookMousePlane.Raycast(ray, out float d)) sketchbookPointer.position = ray.GetPoint(d);
                    }
                }
#endif
                if (sketchbookDragging) SketchbookEvent(PointerEventType.Move);
            }
            if (OfferingBook && SketchbookHeld)
            {
                takingSketchbook = true;
                StopOfferingBook(true);
                Heard();
            }
            if (takingSketchbook && !SketchbookHeld)
            {
                takingSketchbook = false;
                StartCoroutine(HandSketchbook(true));
            }
        }

        private void SketchbookEvent(PointerEventType type) =>
            sketchbookGrab.ProcessPointerEvent(new PointerEvent(SketchbookPointerId, type, sketchbookPointer));

        private void EndSketchbookGrab()
        {
            if (sketchbookDragging && sketchbookGrab != null)
            {
                SketchbookEvent(PointerEventType.Unselect);
                SketchbookEvent(PointerEventType.Unhover);
            }
            sketchbookDragging = false;
            sketchbookNearDrag = false;
            sketchbookHand = null;
        }

        private void PutSketchbookAway()
        {
            EndSketchbookGrab();
            takingSketchbook = paperInFlight = Drawing = penDown = false;
            drawingHand = null;
            if (paper != null)
            {
                sketchbookNear.enabled = false;
                paper.gameObject.SetActive(false);
            }
        }
    }
}
