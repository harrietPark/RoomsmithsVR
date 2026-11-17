using Meta.XR.MRUtilityKit;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F91 (2026-11-15, the user: "the things I collected before - pictures, photos, notes - put near a wall (or the plan
    // board), a pin comes out and they stick there"). Back online (WindupsCollect.ShowOnline), a collected thing carried
    // within CollectPinReach of a real wall (any vertical MRUK surface at least as tall as the thing) or of the idea wall's
    // board (F72, where the current note is) shows a pushpin over its top; let go, it flies flat onto that wall and the pin
    // pushes in. Its place is kept: on the board board-local, on a wall against the wall's anchor, with the world spot as a
    // fallback; the next start puts it back there, pinned. Taken off again and let go away from any wall, the pin goes.
    // This side of it (where, saving, the pin's model) is here; carrying, the views and the saved list are WindupsCollect's.
    public sealed partial class MakerSession
    {
        public const float CollectPinReach = StickToWall;   // 15 cm to the wall's plane, either side of it
        public const float BoardPinSpan = .8f;              // how far from the board's middle (x the board's scale) it still counts
        private const float PinOffWall = .006f;             // a pinned thing stands this far out of its wall
        private static readonly Vector3[] PinDirections = new Vector3[8];

        public enum PinPlace { None, Wall, Board }

        // Hands the session holds something with (WindupsCollect leaves their pinches alone).
        public bool HandHolds(Hand hand) => HeldBy(hand) != null;

        // The wall or board a thing at `at` (half as tall as `halfHeight`) would be pinned to: its pose there (facing into
        // the wall, like the views), which, the anchor's key for a wall, and how far the wall's plane is.
        public PinPlace CollectPinSpot(Vector3 at, float halfHeight, out Pose pose, out string anchorKey, out float distance)
        {
            pose = default;
            anchorKey = null;
            distance = float.MaxValue;
            var place = PinPlace.None;
            if (board != null && board.gameObject.activeInHierarchy)
            {
                var plane = new Plane(board.forward, board.position);
                float d = Mathf.Abs(plane.GetDistanceToPoint(at));
                Vector3 onPlane = plane.ClosestPointOnPlane(at);
                if (d < CollectPinReach && Vector3.Distance(onPlane, board.position) < BoardPinSpan * BoardScale)
                {
                    distance = d;
                    pose = new Pose(onPlane - board.forward * PinOffWall, board.rotation);
                    place = PinPlace.Board;
                }
            }
            var room = arrival != null ? arrival.handoff.meeting.environment : null;
            if (room == null || !room.IsReady || Head == null) return place;
            Vector3 away = Vector3.ProjectOnPlane(at - Head.position, Vector3.up);
            away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.ProjectOnPlane(Head.forward, Vector3.up).normalized;
            PinDirections[0] = away;
            for (int i = 1; i < PinDirections.Length; i++) PinDirections[i] = Quaternion.AngleAxis(i * 45f, Vector3.up) * away;
            foreach (var dir in PinDirections)
            {
                // From 30 cm before the thing (it may be pushed a little into the wall), toward the wall.
                if (!Physics.Raycast(at - dir * .3f, dir, out var hit, .3f + CollectPinReach, room.GeometryMask, QueryTriggerInteraction.Ignore)) continue;
                if (Mathf.Abs(hit.normal.y) > .3f || Vector3.Dot(hit.normal, dir) > -.5f) continue;
                Vector3 normal = Vector3.ProjectOnPlane(hit.normal, Vector3.up).normalized;
                var wallPlane = new Plane(normal, hit.point);
                float d = Mathf.Abs(wallPlane.GetDistanceToPoint(at));
                if (d >= CollectPinReach || d >= distance) continue;
                Vector3 spot = wallPlane.ClosestPointOnPlane(at);
                // The wall must be there over the thing's whole height (not a desk's front edge).
                if (!WallAt(spot + Vector3.up * halfHeight, normal, hit.collider) || !WallAt(spot - Vector3.up * halfHeight, normal, hit.collider)) continue;
                distance = d;
                pose = new Pose(spot + normal * PinOffWall, Quaternion.LookRotation(-normal, Vector3.up));
                var anchor = hit.collider.GetComponentInParent<MRUKAnchor>();
                anchorKey = anchor != null ? AnchorKey(anchor) : null;
                place = PinPlace.Wall;
            }
            return place;
        }

        private static bool WallAt(Vector3 point, Vector3 normal, Collider wall) =>
            wall.Raycast(new Ray(point + normal * .05f, -normal), out var hit, .1f) && Mathf.Abs(hit.distance - .05f) < .02f;

        // Where a pinned thing is kept: board-local on the board, anchor-local on a wall (null when neither is found).
        public bool CollectPinLocal(PinPlace place, Pose pose, string anchorKey, out float[] local, out float[] turn)
        {
            local = turn = null;
            Transform frame = place == PinPlace.Board ? board : place == PinPlace.Wall ? FindAnchor(anchorKey)?.transform : null;
            if (frame == null) return false;
            Vector3 l = frame.InverseTransformPoint(pose.position);
            Quaternion t = Quaternion.Inverse(frame.rotation) * pose.rotation;
            local = new[] { l.x, l.y, l.z };
            turn = new[] { t.x, t.y, t.z, t.w };
            return true;
        }

        // A kept place back in the room: on the board, by the wall's anchor, else the world spot (false: no room yet).
        public bool CollectPinPose(string place, string anchorKey, float[] local, float[] turn, float[] world, float[] worldTurn, out Pose pose)
        {
            pose = default;
            if (place == "board" && board != null && local != null && local.Length == 3 && turn != null && turn.Length == 4)
            {
                pose = new Pose(board.TransformPoint(new Vector3(local[0], local[1], local[2])), board.rotation * new Quaternion(turn[0], turn[1], turn[2], turn[3]));
                return true;
            }
            if (place == "wall" && PlaceFromAnchor(anchorKey, local, turn, out Vector3 at, out Quaternion rot)) { pose = new Pose(at, rot); return true; }
            var room = arrival != null ? arrival.handoff.meeting.environment : null;
            if (room == null || !room.IsReady || world == null || world.Length != 3) return false;
            var q = worldTurn != null && worldTurn.Length == 4 ? new Quaternion(worldTurn[0], worldTurn[1], worldTurn[2], worldTurn[3]) : Quaternion.identity;
            pose = new Pose(new Vector3(world[0], world[1], world[2]), q);
            return true;
        }

        // The designed pushpin (Resources/WindupsPin; the flat red disc without it), its needle along +Z into the card.
        public Transform MakeCollectPushpin(Transform parent)
        {
            Transform pin;
            var model = Resources.Load<GameObject>("WindupsPin");
            if (model != null)
            {
                pin = Instantiate(model, parent, false).transform;
                pin.localScale = Vector3.one * PushpinScale;
            }
            else
            {
                if (material == null) EnsureCrew();
                var disc = StickerMesh("pushpin", () => ToyMeshes.Disc(.006f, .004f, new Color(.95f, .35f, .3f), new Color(.75f, .25f, .2f), "Pushpin"));
                Part(parent, "Pushpin", disc, Vector3.zero, 0f);
                pin = parent.Find("Pushpin");
            }
            pin.name = "Pushpin";
            foreach (var r in pin.GetComponentsInChildren<Renderer>())
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            return pin;
        }
    }
}
