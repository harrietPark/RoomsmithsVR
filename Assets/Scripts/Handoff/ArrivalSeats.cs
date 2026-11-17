using System.Collections.Generic;
using RoomCapsule;
using UnityEngine;

namespace Windups.Handoff
{
    internal sealed class ArrivalSeats
    {
        private struct Seat
        {
            public Vector3 Point;
            public CapsuleRoomSurface Surface;
        }

        private readonly List<Seat> seats = new List<Seat>();
        private CapsuleRoomSurface preferred;
        private Vector3 head;
        private Vector3 forward;
        private Vector3 furnitureCenter;

        public ArrivalSeats(CapsuleRoomEnvironment room, Vector3 head, Vector3 forward)
        {
            this.head = head;
            this.forward = forward;
            float nearestFurniture = float.PositiveInfinity;
            foreach (var surface in room.Surfaces)
            {
                if (surface.Polygon == null || surface.Polygon.Length < 3) continue;
                Vector2 min = surface.Polygon[0], max = min;
                foreach (var vertex in surface.Polygon)
                {
                    min = Vector2.Min(min, vertex);
                    max = Vector2.Max(max, vertex);
                }
                for (float x = Mathf.Max(min.x + .07f, head.x - 1.8f); x <= Mathf.Min(max.x, head.x + 1.8f); x += .14f)
                for (float z = Mathf.Max(min.y + .07f, head.z - 1.8f); z <= Mathf.Min(max.y, head.z + 1.8f); z += .14f)
                {
                    var footprint = new Vector2(x, z);
                    if (!surface.Contains(footprint, .06f)) continue;
                    var point = surface.Point(footprint);
                    Vector3 horizontal = Vector3.ProjectOnPlane(point - head, Vector3.up);
                    float distance = horizontal.magnitude;
                    if (distance < .38f || distance > 1.8f || point.y > head.y - .27f ||
                        Vector3.Dot(horizontal, forward) < .06f ||
                        !room.TrySameLevelNavPoint(point, out var nav) ||
                        Physics.Linecast(head, nav + Vector3.up * .16f, room.GeometryMask, QueryTriggerInteraction.Ignore))
                        continue;

                    seats.Add(new Seat { Point = nav, Surface = surface });
                    float reach = Vector3.Distance(head, nav);
                    if (!surface.IsFloor && distance <= 1.2f && reach < nearestFurniture)
                    {
                        nearestFurniture = reach;
                        preferred = surface;
                        furnitureCenter = nav;
                    }
                }
            }
        }

        public List<Vector3> Ranked(float angle, CapsuleRoomEnvironment room = null)
        {
            Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * forward;
            Vector3 desired = head + direction * .85f;
            var ranked = new List<Seat>(seats);
            if (room != null) ranked.RemoveAll(seat => !HasPaperClearance(room, seat.Point));
            ranked.Sort((a, b) => Score(a, desired).CompareTo(Score(b, desired)));
            return ranked.ConvertAll(seat => seat.Point);
        }

        // Fallback seats closest to a formation seat (used if its own path fails).
        public List<Vector3> RankedNear(Vector3 desired, CapsuleRoomEnvironment room = null)
        {
            var ranked = new List<Seat>(seats);
            // Only seats whose card above the character would be visible (not behind a monitor).
            if (room != null) ranked.RemoveAll(seat => !HasPaperClearance(room, seat.Point));
            ranked.Sort((a, b) => Vector3.ProjectOnPlane(a.Point - desired, Vector3.up).sqrMagnitude
                .CompareTo(Vector3.ProjectOnPlane(b.Point - desired, Vector3.up).sqrMagnitude));
            return ranked.ConvertAll(seat => seat.Point);
        }

        private bool HasPaperClearance(CapsuleRoomEnvironment room, Vector3 point)
        {
            Vector3 center = point + Vector3.up * .30f;
            Vector3 right = Vector3.Cross(Vector3.up, center - head).normalized * .16f;
            return !Physics.Linecast(head, center + right, room.GeometryMask, QueryTriggerInteraction.Ignore) &&
                   !Physics.Linecast(head, center - right, room.GeometryMask, QueryTriggerInteraction.Ignore) &&
                   !Physics.CheckSphere(center, .17f, room.GeometryMask, QueryTriggerInteraction.Ignore);
        }

        private float Score(Seat seat, Vector3 desired)
        {
            Vector3 offset = Vector3.ProjectOnPlane(seat.Point - desired, Vector3.up);
            float score = offset.sqrMagnitude + Vector3.Distance(seat.Point, head) * .22f;
            if (preferred != null)
            {
                if (seat.Surface == preferred) return score;
                score += 5f + Vector3.ProjectOnPlane(seat.Point - furnitureCenter, Vector3.up).sqrMagnitude * .45f;
            }
            return score;
        }
    }
}
