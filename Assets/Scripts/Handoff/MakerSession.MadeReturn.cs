using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Windups.Handoff
{
    // F91 (2026-11-15, Quest: "when I come back, the things I made before must be grab & release; they collide with the
    // MRUK room too, and stay exactly where I let them go"). On the Quest nothing made before came back to be picked up:
    // on the way in (RestoreBeforeTitle) a make whose anchor was not found was skipped, and the restore was then marked
    // done, so the welcome never brought it back. Now every saved make is tracked by its own key (RestoreKey): the way in
    // places what its anchor finds and leaves the rest waiting; the welcome (or the first note) places the rest - by the
    // anchor when it has loaded by then, else at the world spot and turn it was saved at (Made.turn, saved since F91; MRUK's
    // world lock keeps world places from one start to the next). A make put back by its world spot is pushed out of the room
    // and the other made things (ClearOfRoom). Once back, it is an ordinary made thing (MadeThings): a pinch takes it
    // (grab & release, F67), held it cannot go into the room (F38 MoveHeld), let go it stays there, and its new place is
    // saved against the anchor under it.
    public sealed partial class MakerSession
    {
        private readonly HashSet<string> restoredMade = new HashSet<string>();
        public int RestoresWaiting { get; private set; }   // makes the way in left for the welcome (checks)
        public int RestoredByWorld { get; private set; }   // makes put back at their saved world spot

        private static string RestoreKey(Made item) => !string.IsNullOrEmpty(item.id) ? item.id : item.name + "|" + item.savedAt;

        // Where a saved make goes: by its anchor's id (or a name only one anchor has); on the way in a missing anchor waits
        // (it may still be loading); later, the world spot and turn it was saved at.
        private bool RestorePlace(Made item, out Vector3 at, out Quaternion rot, out bool byWorld)
        {
            at = default;
            rot = Quaternion.identity;
            byWorld = false;
            if (item?.design == null) return false;
            if (PlaceFromAnchor(item.anchor, item.local, item.rotation, out at, out rot)) return true;
            if (restoringAtLaunch && !string.IsNullOrEmpty(item.anchor)) { RestoresWaiting++; return false; }
            if (item.world == null || item.world.Length < 3) return false;
            at = new Vector3(item.world[0], item.world[1], item.world[2]);
            // (saved without an anchor, `rotation` is already the world turn)
            float[] q = item.turn != null && item.turn.Length == 4 ? item.turn : string.IsNullOrEmpty(item.anchor) && item.rotation != null && item.rotation.Length == 4 ? item.rotation : null;
            if (q != null)
            {
                var turn = new Quaternion(q[0], q[1], q[2], q[3]);
                float length = Mathf.Sqrt(turn.x * turn.x + turn.y * turn.y + turn.z * turn.z + turn.w * turn.w);
                rot = length > 1e-4f ? new Quaternion(turn.x / length, turn.y / length, turn.z / length, turn.w / length) : Quaternion.identity;
            }
            byWorld = true;
            RestoredByWorld++;
            WriteInputLog("restore " + item.name + ": " + (string.IsNullOrEmpty(item.anchor) ? "no anchor" : "anchor not found") + ", back at its saved world spot", true);
            return true;
        }

        // A make put back by its world spot (the room may have changed since) is pushed out of the room and the other
        // made things, the way a held thing is (F38 PushOut).
        private void ClearOfRoom(Transform thing)
        {
            if (thing == null) return;
            var mine = thing.GetComponentsInChildren<Collider>().FirstOrDefault(c => c.enabled && !c.isTrigger);
            if (mine == null) return;
            EnsureRoomScan();
            Physics.SyncTransforms();
            Vector3 start = thing.position;
            for (int pass = 0; pass < 6; pass++)
            {
                bool moved = false;
                int n = Physics.OverlapSphereNonAlloc(mine.bounds.center, mine.bounds.extents.magnitude + .004f, overlaps, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var other = overlaps[i];
                    if (!Solid(other, thing)) continue;
                    if (!Physics.ComputePenetration(mine, mine.transform.position, mine.transform.rotation, other, other.transform.position, other.transform.rotation, out Vector3 dir, out float dist) || dist < 1e-5f) continue;
                    thing.position += dir * (dist + .001f);
                    moved = true;
                }
                if (!moved) break;
                Physics.SyncTransforms();
            }
            if ((thing.position - start).sqrMagnitude > 1e-8f)
                WriteInputLog("restore: pushed " + (Vector3.Distance(thing.position, start) * 100f).ToString("0.0") + " cm out of the room", true);
        }
    }
}
