using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace Windups.Handoff
{
    // F97 (Quest 2026-11-15, the user: "the post-it went up on the wall beside me; I move it to put it on the wall in
    // front, why doesn't it stick?"). Two causes:
    //  - letting go of the CURRENT note counted as letting it go "on the current note" (NearCurrentNote of itself), so
    //    the idea began again (ShapeInput -> ForObject in the log, four times) and the note flew back to its place;
    //  - a note let go stuck only to the idea board's own plane: any other real wall left it hanging in the air.
    // Now a note let go within StickToWall of a real wall (MRUK WALL_FACE) sticks there, flat on it. The current note
    // takes the whole idea board with it (earlier notes, the pad, kept cards and the pictures pinned on the board), and
    // the board's new wall is what is remembered for next time.
    public sealed partial class MakerSession
    {
        public int BoardMoves { get; private set; }

        // The nearest real wall face within StickToWall of a point (the side facing the point).
        private bool RealWallNear(Vector3 at, out Vector3 point, out Vector3 normal, out MRUKAnchor anchor)
        {
            point = normal = Vector3.zero;
            anchor = null;
            if (arrival == null) return false;
            int mask = arrival.handoff.meeting.environment.GeometryMask;
            float best = StickToWall;
            for (int i = 0; i < 12; i++)
            {
                Vector3 way = Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward;
                if (!Physics.Raycast(at, way, out var hit, StickToWall, mask, QueryTriggerInteraction.Ignore) || Mathf.Abs(hit.normal.y) > .3f) continue;
                var owner = hit.collider.GetComponentInParent<MRUKAnchor>();
                if (owner == null || !owner.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE)) continue;
                Vector3 n = Vector3.ProjectOnPlane(hit.normal, Vector3.up).normalized;
                float distance = Mathf.Abs(Vector3.Dot(at - hit.point, n)); // straight out from the wall, not along the ray
                if (distance >= best) continue;
                best = distance;
                normal = n;
                point = at - n * Vector3.Dot(at - hit.point, n);
                anchor = owner;
            }
            return anchor != null;
        }

        // The current note let go on another real wall: the idea board goes there, the note at its origin.
        private void MoveBoardTo(Vector3 point, Vector3 normal, MRUKAnchor anchor)
        {
            if (board == null) return;
            Vector3 oldPosition = board.position;
            Quaternion oldRotation = board.rotation;
            bool sameWall = Vector3.Dot(normal, -(oldRotation * Vector3.forward)) > .99f &&
                            Mathf.Abs(Vector3.Dot(point - oldPosition, normal)) < .01f;
            var pose = new Pose(sameWall ? point : point + normal * .004f, Quaternion.LookRotation(-normal, Vector3.up));
            // Pictures pinned on the board are not its children: they come along by the same move.
            for (int i = 0; i < PinViews.Count; i++)
            {
                var view = PinViews[i];
                if (view == null || (pinCarry != null && pinCarry.view == view)) continue;
                Vector3 local = Quaternion.Inverse(oldRotation) * (view.position - oldPosition);
                if (Mathf.Abs(local.z) > .06f || new Vector2(local.x, local.y).magnitude > 1.4f) continue; // not on this board
                Quaternion turn = Quaternion.Inverse(oldRotation) * view.rotation;
                view.SetPositionAndRotation(pose.position + pose.rotation * local, pose.rotation * turn);
                if (i < Pins.Count && Pins[i] != null)
                {
                    if (anchor != null) Pins[i].anchor = AnchorKey(anchor);
                    SetPinPlace(Pins[i], view.position, view.rotation);
                }
            }
            SavePins();
            board.SetPositionAndRotation(pose.position, pose.rotation);
            arrival.PutNoteOnPad(PadPose.position, PadPose.rotation, BoardScale);
            var data = Wall;
            if (anchor != null)
            {
                data.place = "wall";
                data.anchor = AnchorKey(anchor);
                Vector3 anchorLocal = anchor.transform.InverseTransformPoint(pose.position);
                Quaternion anchorTurn = Quaternion.Inverse(anchor.transform.rotation) * pose.rotation;
                data.local = new[] { anchorLocal.x, anchorLocal.y, anchorLocal.z };
                data.rotation = new[] { anchorTurn.x, anchorTurn.y, anchorTurn.z, anchorTurn.w };
                SaveWall();
            }
            BoardMoves++;
            if (!sameWall)
            {
                ToySounds.Play(ToySounds.Cue.Press, pose.position, .55f, 1.5f);
                ToyFx.Puff(pose.position, .012f, 5);
            }
            WriteInputLog("idea board moved " + (sameWall ? "along its wall" : "to another wall") + " with the current note (" + (anchor != null ? AnchorKey(anchor) : "no anchor") + ")", true);
        }
    }
}
