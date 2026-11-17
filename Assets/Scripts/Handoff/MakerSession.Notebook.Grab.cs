using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    public sealed partial class MakerSession
    {
        private Grabbable notebookGrab;
        private Hand notebookHand;
        private bool notebookDragging;
        private float notebookDistance;
        private Plane notebookDragPlane;
        private Pose notebookPointer;
        private const int NotebookPointerId = 983612;
        // Only the open notebook, in view, can be held: a grab left over when it was closed (the SDK keeps the
        // point when its object is hidden mid-grab) must not count, or every hand tool stays off (2026-11-12 fix).
        public bool NotebookHeld => NotebookOpen && notebookView != null && notebookView.gameObject.activeInHierarchy &&
                                    (notebookDragging || (notebookGrab != null && notebookGrab.SelectingPointsCount > 0));

        private void BeginNotebookGrab(Hand hand, Ray? ray, Vector3 point, float distance)
        {
            if (notebookGrab == null || NotebookHeld) return;
            notebookHand = hand; notebookDistance = distance;
            notebookDragging = true;
            Vector3 at = ray.HasValue && distance > 0 ? ray.Value.GetPoint(distance) : point;
            notebookPointer = new Pose(at, hand != null ? HandTurn(hand) : Quaternion.identity);
            notebookDragPlane = new Plane(notebookView.forward, at);
            NotebookEvent(PointerEventType.Hover); NotebookEvent(PointerEventType.Select);
        }
        private void UpdateNotebookGrab()
        {
            if (!notebookDragging) return;
            if (!NotebookOpen || notebookGrab == null) { EndNotebookGrab(); return; }
            if (notebookHand != null)
            {
                if (!notebookHand.isActiveAndEnabled || !HandIO.Connected(notebookHand) || !HandIO.Pinching(notebookHand)) { EndNotebookGrab(); return; }
                if (notebookDistance > 0 && HandIO.Pointer(notebookHand, out var pose)) notebookPointer.position = pose.position + pose.forward * notebookDistance;
                else notebookPointer.position = PinchPoint(notebookHand);
                notebookPointer.rotation = HandTurn(notebookHand);
            }
#if UNITY_EDITOR
            else if (!notebookSimulating)
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse == null || !mouse.leftButton.isPressed) { EndNotebookGrab(); return; }
                var ray = Camera.main.ScreenPointToRay(mouse.position.ReadValue());
                if (notebookDragPlane.Raycast(ray, out float d)) notebookPointer.position = ray.GetPoint(d);
            }
#endif
            NotebookEvent(PointerEventType.Move);
        }
        private void NotebookEvent(PointerEventType type) => notebookGrab?.ProcessPointerEvent(new PointerEvent(NotebookPointerId, type, notebookPointer));
        private void EndNotebookGrab()
        {
            if (notebookDragging) { NotebookEvent(PointerEventType.Unselect); NotebookEvent(PointerEventType.Unhover); RememberNotebookPose(); }
            notebookDragging = false; notebookHand = null;
        }
#if UNITY_EDITOR
        private bool notebookSimulating;
        public void SimulateNotebookGrab(Pose pose, bool release = false)
        {
            notebookSimulating = true;
            if (!notebookDragging) BeginNotebookGrab(null, null, pose.position, 0);
            notebookPointer = pose; NotebookEvent(PointerEventType.Move);
            if (release) { EndNotebookGrab(); notebookSimulating = false; }
        }
#endif
    }
}
