using System.Linq;
using UnityEngine;
using DeskSurface = RoomCapsule.CapsuleRoomSurface;

namespace Windups.Handoff
{
    // (2026-11-12, after a Quest try: "if I work at the desk, keep working at the desk; don't suddenly put things
    // on the floor".) The working desk is the furniture top at working height (0.2-1.25 m below the eyes) in
    // front of the person, the crew's own surface first when they stand on furniture. Things the session puts
    // down (the build spot, the show-me circle, versions stepping aside, the v2 twin, a thing that fell
    // through the room) go on it: the point asked for when it is on the desk, else the nearest point of the
    // desk to it. The floor is used only when there is no desk at all.
    public sealed partial class MakerSession
    {
        public DeskSurface WorkSurface => FindWorkSurface();
        private DeskSurface workSurface;
        private float workSurfaceAt = -1f;

        private DeskSurface FindWorkSurface()
        {
            if (workSurface != null && Time.time < workSurfaceAt) return workSurface;
            workSurfaceAt = Time.time + 1.5f;
            var room = arrival != null ? arrival.handoff.meeting.environment : null;
            if (room == null || room.Surfaces == null || Head == null) return workSurface = null;
            Vector3 front = FrontPoint(.4f, -.35f, out _);
            var q = new Vector2(front.x, front.z);
            // Where the crew stands (the meeting is held there).
            var feet = cast.Values.Where(c => c != null).Select(c => c.transform.position).OrderBy(p => p.y).ToList();
            Vector3? crew = feet.Count > 0 ? feet[feet.Count / 2] : (Vector3?)null;
            DeskSurface best = null;
            float bestScore = float.MaxValue;
            foreach (var s in room.Surfaces)
            {
                if (s == null || s.IsFloor || s.Polygon == null || s.Polygon.Length < 3) continue;
                Vector2 near = ClosestOn(s, q, 0f);
                float y = s.Point(near).y;
                if (y > Head.position.y - .2f || y < Head.position.y - 1.25f) continue;
                float score = Vector2.Distance(near, q);
                if (score > 1.2f) continue;
                if (crew.HasValue && s.Contains(new Vector2(crew.Value.x, crew.Value.z), -.02f) && Mathf.Abs(crew.Value.y - y) < .06f) score -= .6f;
                if (score < bestScore) { bestScore = score; best = s; }
            }
            return workSurface = best;
        }

        // The point on the working desk nearest `want` (at least `margin` in from its edges), else `want` on
        // whatever surface is under it.
        public Vector3 WorkPoint(Vector3 want, float margin = .05f)
        {
            var s = FindWorkSurface();
            if (s == null) return new Vector3(want.x, DeskHeightAt(want, want.y), want.z);
            var q = ClosestOn(s, new Vector2(want.x, want.z), margin);
            return s.Point(q);
        }

        public bool OnWorkDesk(Vector3 p, float margin = 0f)
        {
            var s = FindWorkSurface();
            return s != null && s.Contains(new Vector2(p.x, p.z), margin) && Mathf.Abs(s.Point(new Vector2(p.x, p.z)).y - p.y) < .05f;
        }

        // The point of the polygon nearest q, pulled `margin` inside (toward its middle) when q is outside.
        private static Vector2 ClosestOn(DeskSurface s, Vector2 q, float margin)
        {
            if (s.Contains(q, margin)) return q;
            var poly = s.Polygon;
            Vector2 best = poly[0];
            float bestD = float.MaxValue;
            for (int i = 0; i < poly.Length; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Length], ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(q - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
                Vector2 c = a + ab * t;
                float d = (c - q).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            Vector2 middle = poly.Aggregate(Vector2.zero, (sum, p) => sum + p) / poly.Length;
            Vector2 inward = middle - best;
            float room = inward.magnitude;
            if (room < 1e-5f) return best;
            for (float step = margin; step <= room; step += Mathf.Max(.01f, margin * .5f))
            {
                Vector2 p = best + inward / room * step;
                if (s.Contains(p, margin)) return p;
            }
            return best + inward * .5f;
        }
    }
}
