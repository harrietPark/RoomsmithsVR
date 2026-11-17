using System.Collections.Generic;
using RoomCapsule;
using UnityEngine;

namespace Windups.Handoff
{
    // Where the meeting happens: one spot on the nearest suitable surface in front of the
    // user (a desk or table first, the floor only when no furniture fits), with the topic
    // ring in the middle and the six characters on an arc behind it, facing the user.
    // Chosen once at the double clap so the gathering already leaves room for the ring.
    public sealed class GatherFormation
    {
        // Rejection counters from the last Find, for Editor diagnostics.
        public static string LastReport { get; private set; } = "";
        public static string AroundReport { get; private set; } = "";
        private static int rejectRing, rejectSeats, rejectSight, rejectNav, rejectSeatSight;
        // Preferred sizes, set from WindupsArrival's Inspector fields before each search. The
        // widest spacing and largest ring that fit are used; smaller ones are tried in turn.
        public struct Layout { public float DeskRing, DeskGap, FloorRing, FloorGap, ArcSpan, MinSpacing; public bool AroundUser; }
        public static Layout Settings = new Layout { DeskRing = .16f, DeskGap = .26f, FloorRing = .24f, FloorGap = .3f, ArcSpan = 170f, MinSpacing = .14f };

        // Personal space (2026-11-11): the crew stood as close as 14 cm (5.5 cm when two voted for the same
        // picture, 13 cm after walking back), so heads, name tags and speech cards overlapped. Neighbours now
        // stand at least CrewSpacing apart and, seen from the person's eyes, at least CrewAngle apart (a speech
        // card at arm's length is about that wide). A desk too small for that still keeps the crew on it with
        // the old minimum (Settings.MinSpacing) rather than sending it to the floor.
        public const float CrewSpacing = .22f, CrewAngle = 17f;
        private static bool roomyPass;

        public static float AngleSeen(Vector3 head, Vector3 a, Vector3 b) =>
            Vector3.Angle(Vector3.ProjectOnPlane(a - head, Vector3.up), Vector3.ProjectOnPlane(b - head, Vector3.up));

        public static bool Roomy(Vector3 head, Vector3 a, Vector3 b) =>
            Vector3.Distance(a, b) >= CrewSpacing && AngleSeen(head, a, b) >= CrewAngle;

        // A free spot for one character near `want`, on the same level, seen from the person, and roomy
        // next to `others` (else at least Settings.MinSpacing from them). Nearest to `want` first.
        public static bool FreeSpotNear(CapsuleRoomEnvironment room, Vector3 head, Vector3 want, IList<Vector3> others, float reach, out Vector3 spot)
        {
            spot = want;
            foreach (bool roomy in new[] { true, false })
            {
                for (float r = 0f; r <= reach + .001f; r += .04f)
                {
                    int steps = r < .01f ? 1 : Mathf.Max(8, Mathf.CeilToInt(2f * Mathf.PI * r / .04f));
                    float bestDistance = float.PositiveInfinity;
                    bool found = false;
                    for (int i = 0; i < steps; i++)
                    {
                        float a = i * 2f * Mathf.PI / steps;
                        Vector3 probe = want + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                        if (!room.TrySameLevelNavPoint(probe, out var nav) || Mathf.Abs(nav.y - want.y) > .04f) continue;
                        bool ok = true;
                        foreach (var o in others)
                            if (Vector3.Distance(o, nav) < Settings.MinSpacing || (roomy && !Roomy(head, o, nav))) { ok = false; break; }
                        if (!ok) continue;
                        if (Physics.Linecast(head, nav + Vector3.up * .12f, room.GeometryMask, QueryTriggerInteraction.Ignore)) continue;
                        // On a ring, prefer the side toward the person.
                        float d = Vector3.Distance(nav, head);
                        if (d < bestDistance) { bestDistance = d; spot = nav; found = true; }
                    }
                    if (found) return true;
                }
            }
            return false;
        }

        public Vector3 Center;
        public float RingRadius;
        public Vector3[] Seats;
        public bool OnFurniture;
        public string SurfaceName;

        private struct Option
        {
            public CapsuleRoomSurface Surface;
            public Vector2 Center;
            public float Score;
        }

        public static GatherFormation Find(CapsuleRoomEnvironment room, Vector3 head, Vector3 forward, int count)
        {
            rejectRing = rejectSeats = rejectSight = rejectNav = rejectSeatSight = 0;
            if (Settings.AroundUser)
            {
                var around = FindAroundUser(room, head, forward, count);
                if (around != null) return around;
                AroundReport = "ring=" + rejectRing + " seats=" + rejectSeats + " sight=" + rejectSight + " nav=" + rejectNav + " seatSight=" + rejectSeatSight;
                rejectRing = rejectSeats = rejectSight = rejectNav = rejectSeatSight = 0;
            }
            var options = new List<Option>();
            foreach (var surface in room.Surfaces)
            {
                if (surface.Polygon == null || surface.Polygon.Length < 3) continue;
                Vector2 min = surface.Polygon[0], max = min;
                foreach (var vertex in surface.Polygon) { min = Vector2.Min(min, vertex); max = Vector2.Max(max, vertex); }
                for (float x = Mathf.Max(min.x, head.x - 1.45f); x <= Mathf.Min(max.x, head.x + 1.45f); x += .06f)
                for (float z = Mathf.Max(min.y, head.z - 1.45f); z <= Mathf.Min(max.y, head.z + 1.45f); z += .06f)
                {
                    var c = new Vector2(x, z);
                    if (!surface.Contains(c, .13f)) continue;
                    Vector3 point = surface.Point(c);
                    Vector3 horizontal = Vector3.ProjectOnPlane(point - head, Vector3.up);
                    float distance = horizontal.magnitude;
                    if (distance < .42f || distance > 1.4f || point.y > head.y - .25f) continue;
                    float facing = Vector3.Dot(horizontal / distance, forward);
                    if (facing < .5f) continue;
                    // Furniture strongly preferred; then about an arm-and-a-half away, straight ahead.
                    float score = (surface.IsFloor ? 4f : 0f) + Mathf.Abs(distance - .72f) * 1.4f + (1f - facing) * 2f;
                    options.Add(new Option { Surface = surface, Center = c, Score = score });
                }
            }
            options.Sort((a, b) => a.Score.CompareTo(b.Score));
            rejectRing = rejectSeats = rejectSight = rejectNav = rejectSeatSight = 0;
            int checkedOptions = 0, furniture = 0;
            foreach (var option in options) if (!option.Surface.IsFloor) furniture++;
            foreach (var option in options)
            {
                if (++checkedOptions > 80) break;
                var formation = TryOption(room, head, option, count);
                if (formation != null)
                {
                    if (count <= 3) Tighten(room, head, formation);
                    LastReport = "found " + formation.SurfaceName + " after " + checkedOptions;
                    return formation;
                }
            }
            LastReport = "options=" + options.Count + " furnitureOptions=" + furniture + " ring=" + rejectRing + " seats=" + rejectSeats +
                         " sight=" + rejectSight + " nav=" + rejectNav + " seatSight=" + rejectSeatSight;
            return null;
        }

        private static GatherFormation TryOption(CapsuleRoomEnvironment room, Vector3 head, Option option, int count)
        {
            var surface = option.Surface;
            Vector3 center = surface.Point(option.Center);
            Vector3 toward = Vector3.ProjectOnPlane(center - head, Vector3.up).normalized;
            // Same sight height as the seat checks: the note floats above the ring centre.
            if (Physics.Linecast(head, center + Vector3.up * .12f, room.GeometryMask, QueryTriggerInteraction.Ignore)) { rejectSight++; return null; }
            var layout = Settings;
            float ringSize = Mathf.Max(.06f, surface.IsFloor ? layout.FloorRing : layout.DeskRing);
            float gapSize = Mathf.Max(.05f, surface.IsFloor ? layout.FloorGap : layout.DeskGap);
            float span0 = Mathf.Clamp(layout.ArcSpan, 60f, 220f);
            float[] rings = { ringSize, ringSize * .85f, ringSize * .7f, ringSize * .6f };
            // Never squeeze below ~70% of the requested gap: a narrow desk falls back to the floor
            // (or a turned arc) instead of crowding the characters.
            float[] gaps = { gapSize, gapSize * .85f, gapSize * .7f };
            // F97 (the user: "they gather too far apart"): with three (the maker crew) the 170 deg arc put neighbours ~50 cm
            // apart on a desk. Arcs that keep them about 25 cm apart come first; the wide ones only when those do not fit.
            float[] spans = count <= 3 ? new[] { 70f, 80f, 62f, 92f, span0, span0 * .88f, span0 * .76f } : new[] { span0, span0 * .88f, span0 * .76f };
            var seats = new Vector2[count];
            // Widest spacing first. Open toward the user when possible; otherwise turn the arc to
            // follow a long, narrow free strip (for example the space in front of a monitor).
            foreach (float gap in gaps)
            foreach (float turn in new[] { 0f, -35f, 35f, -65f, 65f })
            foreach (float ring in rings)
            {
                Vector3 away = Quaternion.AngleAxis(turn, Vector3.up) * toward;
                // The ring itself must lie on the surface.
                bool ringFits = true;
                for (int i = 0; i < 12 && ringFits; i++)
                {
                    float a = i * Mathf.PI / 6f;
                    ringFits = surface.Contains(option.Center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * ring, .015f);
                }
                if (!ringFits) { rejectRing++; continue; }
                // A wide, shallow arc (ellipse) behind the ring: desks are often only a
                // strip deep in front of a monitor, so depth is kept small and width large.
                float lateral = ring + gap;
                float depth = ring + gap * (surface.IsFloor ? .5f : .47f);
                Vector3 side = Vector3.Cross(Vector3.up, away);
                foreach (float span in spans)
                {
                    bool fits = true;
                    for (int k = 0; k < count && fits; k++)
                    {
                        float angle = (count == 1 ? 0f : Mathf.Lerp(-span * .5f, span * .5f, k / (count - 1f))) * Mathf.Deg2Rad;
                        Vector3 offset = side * Mathf.Sin(angle) * lateral + away * Mathf.Cos(angle) * depth;
                        seats[k] = option.Center + new Vector2(offset.x, offset.z);
                        fits = surface.Contains(seats[k], .055f);
                    }
                    if (!fits) { rejectSeats++; continue; }
                    var formation = Validate(room, head, surface, center, ring, seats);
                    if (formation != null) return formation;
                }
            }
            return null;
        }

        // F25 studio: the characters stand on a semicircle centred on the user, about an arm and a
        // half away, all on one surface (a desk first, then the floor). The ring, and later the
        // idea card, sit inside the semicircle straight ahead, so nothing is in the card's way.
        private static GatherFormation FindAroundUser(CapsuleRoomEnvironment room, Vector3 head, Vector3 forward, int count)
        {
            var surfaces = new List<CapsuleRoomSurface>();
            // (2026-11-12) Furniture only: an arc that does not fit on the desk is not moved to the floor; the
            // regular layout on the desk (Find) comes next, and the floor only when no furniture fits at all.
            foreach (var surface in room.Surfaces)
                if (surface.Polygon != null && surface.Polygon.Length >= 3 && !surface.IsFloor) surfaces.Add(surface);
            // Among furniture, the one closest to the user.
            Vector2 h = new Vector2(head.x, head.z);
            surfaces.Sort((a, b) =>
            {
                int floor = (a.IsFloor ? 1 : 0).CompareTo(b.IsFloor ? 1 : 0);
                return floor != 0 ? floor : Nearest(a, h).CompareTo(Nearest(b, h));
            });
            // Each character keeps its direction around the user (left, middle, right) and may
            // stand a little nearer or farther so the arc fits on a real desk.
            float[] radii = { .7f, .62f, .78f, .56f, .86f, .5f, .95f };
            float[] turns = { 0f, -7.5f, 7.5f, -15f, 15f, -22.5f, 22.5f, -30f, 30f, -37.5f, 37.5f, -45f, 45f };
            // F97 (Quest 2026-11-15, the user: "when they gather they are too far apart, closer please"): the widest arc came
            // first (110 deg at 0.7 m put neighbours ~65 cm apart). Now the closest arc that still keeps personal space
            // (CrewSpacing 22 cm, CrewAngle 17 deg in the roomy pass) comes first: 40 deg = about 24 cm apart.
            float[] spans = { 40f, 46f, 54f, 34f, 62f, 72f, 84f, 96f, 110f };
            float[] rings = { .12f, .1f, .08f };
            float[] ringAt = { .6f, .7f, .5f, .8f };
            var seats = new Vector2[count];
            try
            {
            foreach (var surface in surfaces)
            foreach (bool roomy in new[] { true, false })
            foreach (float span in spans)
            foreach (bool sameDistance in new[] { true, false })
            foreach (float turn in turns)
            {
                roomyPass = roomy;
                Vector3 d3 = Quaternion.AngleAxis(turn, Vector3.up) * forward;
                Vector2 dir = new Vector2(d3.x, d3.z).normalized;
                bool fits = false;
                float nearest = float.PositiveInfinity;
                // Same distance for all first (a clean arc), then each its own distance.
                foreach (float common in sameDistance ? radii : new[] { 0f })
                {
                fits = true;
                nearest = float.PositiveInfinity;
                for (int k = 0; k < count && fits; k++)
                {
                    float angle = count == 1 ? 0f : Mathf.Lerp(-span * .5f, span * .5f, k / (count - 1f));
                    Vector3 a3 = Quaternion.AngleAxis(angle, Vector3.up) * d3;
                    Vector2 way = new Vector2(a3.x, a3.z).normalized;
                    fits = false;
                    foreach (float radius in common > 0f ? new[] { common } : radii)
                    {
                        Vector2 seat = h + way * radius;
                        if (!surface.Contains(seat, .05f)) continue;
                        Vector3 point = surface.Point(seat);
                        if (point.y > head.y - .25f || !room.TrySameLevelNavPoint(point, out var nav) ||
                            Vector2.Distance(new Vector2(nav.x, nav.z), seat) > .085f || Mathf.Abs(nav.y - point.y) > .1f) continue;
                        seats[k] = seat;
                        nearest = Mathf.Min(nearest, radius);
                        fits = true;
                        break;
                    }
                }
                if (fits) break;
                }
                if (!fits) { rejectSeats++; continue; }
                // The ring (and later the card) inside the arc, straight ahead.
                Vector2 ringCenter = default;
                float ringSize = 0f;
                foreach (float r in rings)
                {
                    foreach (float at in ringAt)
                    {
                        var c = h + dir * nearest * at;
                        if (Vector2.Distance(c, h) < .3f || !CircleFits(surface, c, r)) continue;
                        ringCenter = c;
                        ringSize = r;
                        break;
                    }
                    if (ringSize > 0f) break;
                }
                if (ringSize <= 0f) { rejectRing++; continue; }
                Vector3 center = surface.Point(ringCenter);
                if (Physics.Linecast(head, center + Vector3.up * .12f, room.GeometryMask, QueryTriggerInteraction.Ignore)) { rejectSight++; continue; }
                var formation = Validate(room, head, surface, center, ringSize, seats);
                if (formation == null || !Connected(formation.Seats)) continue;
                LastReport = "around user on " + surface.Name + " span=" + span + " turn=" + turn + (roomy ? " roomy" : " tight");
                return formation;
            }
            }
            finally { roomyPass = false; }
            return null;
        }

        // All seats can walk to each other on the surface (no seat cut off behind a monitor).
        private static readonly UnityEngine.AI.NavMeshPath path = new UnityEngine.AI.NavMeshPath();
        private static bool Connected(Vector3[] points)
        {
            for (int k = 1; k < points.Length; k++)
            {
                if (!UnityEngine.AI.NavMesh.CalculatePath(points[0], points[k], UnityEngine.AI.NavMesh.AllAreas, path) ||
                    path.status != UnityEngine.AI.NavMeshPathStatus.PathComplete) { rejectNav++; return false; }
                float length = 0f;
                for (int i = 1; i < path.corners.Length; i++) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                // A walk far around (down to the floor and back up) means another surface.
                if (length > Vector3.Distance(points[0], points[k]) * 2.2f + .2f) { rejectNav++; return false; }
            }
            return true;
        }

        private static bool CircleFits(CapsuleRoomSurface surface, Vector2 center, float radius)
        {
            if (!surface.Contains(center, .015f)) return false;
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI / 6f;
                if (!surface.Contains(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, .015f)) return false;
            }
            return true;
        }

        private static float Nearest(CapsuleRoomSurface surface, Vector2 point)
        {
            float best = float.PositiveInfinity;
            foreach (var vertex in surface.Polygon) best = Mathf.Min(best, Vector2.Distance(vertex, point));
            return best;
        }

        // The expensive checks (navigation and sight lines) only for the best few layouts.
        // F97 (the user: "they gather too far apart, closer please"): a shallow desk only fits the wide arc behind the
        // ring (neighbours ~30-50 cm apart). With three, each outer seat is then drawn toward the middle one to about
        // CloseSpacing, where it can stand (walkable, seen, room for its card, roomy next to the others).
        public const float CloseSpacing = .25f, CloseAngle = 13f;
        public static int Tightened { get; private set; }
        public static readonly int[] TightenRejects = new int[3]; // nav, sight/card room, spacing (checks)
        private static void Tighten(CapsuleRoomEnvironment room, Vector3 head, GatherFormation formation)
        {
            var seats = formation.Seats;
            if (seats == null || seats.Length < 2) return;
            int mid = seats.Length / 2;
            int mask = room.GeometryMask;
            for (int k = 0; k < seats.Length; k++)
            {
                if (k == mid) continue;
                Vector3 from = seats[mid], to = seats[k];
                Vector3 flat = Vector3.ProjectOnPlane(to - from, Vector3.up);
                if (flat.magnitude <= CloseSpacing + .02f) continue;
                for (float d = CloseSpacing; d < flat.magnitude - .02f; d += .02f)
                {
                    Vector3 want = from + flat.normalized * d;
                    want.y = to.y;
                    if (!room.TrySameLevelNavPoint(want, out var nav) || Mathf.Abs(nav.y - to.y) > .03f ||
                        Vector3.ProjectOnPlane(nav - want, Vector3.up).magnitude > .03f) { TightenRejects[0]++; continue; }
                    if (Physics.Linecast(head, nav + Vector3.up * .12f, mask, QueryTriggerInteraction.Ignore) ||
                        Physics.CheckSphere(nav + Vector3.up * .18f, .04f, mask, QueryTriggerInteraction.Ignore) ||
                        Physics.CheckSphere(nav + Vector3.up * .26f, .04f, mask, QueryTriggerInteraction.Ignore)) { TightenRejects[1]++; continue; }
                    // Close, but each still its own place as seen from the eyes (cards about a hand apart).
                    bool ok = true;
                    for (int j = 0; j < seats.Length && ok; j++)
                        if (j != k && (Vector3.ProjectOnPlane(seats[j] - nav, Vector3.up).magnitude < CloseSpacing - .015f || AngleSeen(head, seats[j], nav) < CloseAngle)) ok = false;
                    if (!ok) { TightenRejects[2]++; continue; }
                    seats[k] = nav;
                    Tightened++;
                    break;
                }
            }
        }

        private static GatherFormation Validate(CapsuleRoomEnvironment room, Vector3 head, CapsuleRoomSurface surface,
            Vector3 center, float ring, Vector2[] seats)
        {
            int mask = room.GeometryMask;
            var points = new Vector3[seats.Length];
            for (int k = 0; k < seats.Length; k++)
            {
                Vector3 seat = surface.Point(seats[k]);
                if (!room.TrySameLevelNavPoint(seat, out var nav) ||
                    Vector2.Distance(new Vector2(nav.x, nav.z), seats[k]) > .085f || Mathf.Abs(nav.y - seat.y) > .1f) { rejectNav++; return null; }
                if (Physics.Linecast(head, nav + Vector3.up * .12f, mask, QueryTriggerInteraction.Ignore) ||
                    // Room for the card above the character (no monitor or shelf right behind it).
                    // The card leans back ~3 cm, so it needs only a few centimetres behind it.
                    Physics.CheckSphere(nav + Vector3.up * .18f, .04f, mask, QueryTriggerInteraction.Ignore) ||
                    Physics.CheckSphere(nav + Vector3.up * .26f, .04f, mask, QueryTriggerInteraction.Ignore)) { rejectSeatSight++; return null; }
                points[k] = nav;
                // Seats snap to the walkable area; never let two characters end up crowded.
                for (int j = 0; j < k; j++)
                    if (Vector3.Distance(points[j], nav) < Settings.MinSpacing || (roomyPass && !Roomy(head, points[j], nav))) { rejectNav++; return null; }
            }
            return new GatherFormation
            {
                Center = center,
                RingRadius = ring,
                Seats = points,
                OnFurniture = !surface.IsFloor,
                SurfaceName = surface.Name
            };
        }
    }
}
