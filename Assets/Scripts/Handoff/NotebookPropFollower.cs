using UnityEngine;

namespace Windups.Handoff
{
    // After WindupBody's late hand pose (order 200): the handle stays in the actual mitten even
    // while Hold is easing in/out. No one-frame floating prop or animation/IK disagreement.
    [DefaultExecutionOrder(300)]
    public sealed class NotebookPropFollower : MonoBehaviour
    {
        public Transform hand;
        public Vector3 handLocal;
        public Quaternion rotation;
        public Transform rotationFrame;
        public Vector3 gripLocal;
        private void LateUpdate()
        {
            if (hand == null) return;
            var turn = rotationFrame != null ? rotationFrame.rotation * rotation : rotation;
            transform.SetPositionAndRotation(hand.TransformPoint(handLocal) - turn * gripLocal, turn);
        }
    }
}
