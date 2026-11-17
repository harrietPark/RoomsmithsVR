using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace RoomCapsule
{
    /// <summary>Top footprint used for semantic furniture traversal, never for walking on walls.</summary>
    public sealed class CapsuleRoomSurface
    {
        public MRUKAnchor Anchor;
        public Vector2[] Polygon;
        public Vector3 Origin;
        public Vector3 Normal;
        public bool IsFloor;
        public string Name => Anchor != null ? Anchor.name : "Missing anchor";
        public float HeightAt(Vector2 p) => Origin.y - (Normal.x * (p.x - Origin.x) + Normal.z * (p.y - Origin.z)) / Normal.y;
        public Vector3 Point(Vector2 p) => new Vector3(p.x, HeightAt(p), p.y);

        public bool Contains(Vector2 p, float margin)
        {
            if (Polygon == null || Polygon.Length < 3) return false;
            for (int i = 0; i < Polygon.Length; i++)
            {
                Vector2 a = Polygon[i], b = Polygon[(i + 1) % Polygon.Length];
                if (Cross(b - a, p - a) < margin * (b - a).magnitude) return false;
            }
            return true;
        }

        public static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        public static Vector2[] ConvexHull(List<Vector2> points)
        {
            points.Sort((a, b) => a.x == b.x ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            var unique = new List<Vector2>();
            foreach (var p in points)
                if (unique.Count == 0 || (p - unique[unique.Count - 1]).sqrMagnitude > 0.000001f) unique.Add(p);
            if (unique.Count < 3) return unique.ToArray();
            var hull = new List<Vector2>();
            foreach (var p in unique)
            {
                while (hull.Count > 1 && Cross(hull[hull.Count - 1] - hull[hull.Count - 2], p - hull[hull.Count - 1]) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            int lower = hull.Count;
            for (int i = unique.Count - 2; i >= 0; i--)
            {
                var p = unique[i];
                while (hull.Count > lower && Cross(hull[hull.Count - 1] - hull[hull.Count - 2], p - hull[hull.Count - 1]) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
            return hull.ToArray();
        }
    }

    public enum CapsuleTraversalKind { Climb, Drop }

}
