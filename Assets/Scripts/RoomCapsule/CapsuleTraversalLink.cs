using UnityEngine;

namespace RoomCapsule
{
    /// <summary>Directed traversal metadata. Furniture sides are never ordinary walkable surfaces.</summary>
    public sealed class CapsuleTraversalLink : MonoBehaviour
    {
        public CapsuleTraversalKind Kind;
        public Vector3[] Waypoints;
        public string Furniture;
        // A climb done as one jump: Waypoints = { take-off, top }, flying a parabola Apex metres above the top.
        public bool Jump;
        public float Apex;
        public Vector3 Start => Waypoints[0];
        public Vector3 End => Waypoints[Waypoints.Length - 1];
    }
}
