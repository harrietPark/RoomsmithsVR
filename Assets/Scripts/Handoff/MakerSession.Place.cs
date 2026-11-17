using System;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace Windups.Handoff
{
    // F38 (2026-11-12): a made thing stays exactly where it is let go (no gravity, no throw), and while it is
    // held it cannot go into the real room. It stops at the MRUK surfaces (desk, walls, floor, furniture), at
    // the room scan (the Quest's global mesh, when the room has one) and at the other things made, and slides
    // along them: the hand may go through the desk, the thing stays on top. Its place is saved against the
    // MRUK anchor under it by the anchor's id (several anchors share a name).
    public sealed partial class MakerSession
    {
        public const string RoomScanLayerName = "RoomScan";
        public bool RoomScanReady => roomScan != null;
        public int RoomScanVertices => roomScan != null && roomScan.sharedMesh != null ? roomScan.sharedMesh.vertexCount : 0;
        public int Contacts { get; private set; }          // holds that met something solid (tests)
        public bool Touching { get; private set; }         // the held thing touches something right now

        private MeshCollider roomScan;
        private MRUKRoom scannedRoom;
        private bool warnedScanLayer;
        private readonly Collider[] overlaps = new Collider[32];

        // The room scan as a collider only the held things meet: on its own layer, which the physics
        // simulation ignores (the crew, the paper balls and the navigation never see it).
        private void EnsureRoomScan()
        {
            var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
            if (room == scannedRoom && (roomScan != null || room == null)) return;
            if (roomScan != null) Destroy(roomScan.gameObject);
            roomScan = null;
            scannedRoom = room;
            int layer = LayerMask.NameToLayer(RoomScanLayerName);
            if (layer < 0)
            {
                if (!warnedScanLayer) Debug.LogWarning("[Windups Place] no " + RoomScanLayerName + " layer: held things meet the MRUK surfaces only.");
                warnedScanLayer = true;
                return;
            }
            var anchor = room != null ? room.GlobalMeshAnchor : null;
            if (anchor == null) return;
            Mesh mesh = null;
            try { mesh = anchor.GlobalMesh; }
            catch (Exception exception) { Debug.LogWarning("[Windups Place] room scan: " + exception.Message); }
            if (mesh == null || mesh.vertexCount < 3) return;
            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(layer, i, true);
            var go = new GameObject("Room Scan Collider") { layer = layer };
            go.transform.SetParent(anchor.transform, false);
            roomScan = go.AddComponent<MeshCollider>();
            roomScan.sharedMesh = mesh;
            Debug.Log("[Windups Place] room scan collider: " + mesh.vertexCount + " vertices");
        }

        // What a held thing cannot go into: the room (MRUK and the scan) and the other things made.
        private bool Solid(Collider other, Transform self)
        {
            if (other == null || other.isTrigger || !other.enabled) return false;
            if (self != null && other.transform.IsChildOf(self)) return false;
            // F102: a carried stand never pushes against what it carries.
            if (self != null && IsStand(self)) foreach (var r in riders) if (r.thing != null && other.transform.IsChildOf(r.thing)) return false;
            if (other == roomScan) return true;
            var room = arrival.handoff.meeting.environment;
            if (room != null && (room.GeometryMask & (1 << other.gameObject.layer)) != 0) return true;
            foreach (var made in MadeThings())
                if (made != self && other.transform.IsChildOf(made)) return true;
            return false;
        }

        // Moves the held thing toward the hand's pose in small steps; each step is pushed back out of whatever
        // it touches, so it rests on surfaces and slides along them.
        private void MoveHeld(Vector3 target, Quaternion turn)
        {
            if (held == null) return;
            var mine = held.GetComponentsInChildren<Collider>().FirstOrDefault(c => c.enabled && !c.isTrigger);
            if (mine == null) { held.SetPositionAndRotation(target, turn); return; }
            Bounds b = mine.bounds;
            float stepSize = Mathf.Max(.005f, Mathf.Min(b.size.x, Mathf.Min(b.size.y, b.size.z)) * .35f);
            Vector3 pos = held.position;
            Quaternion from = held.rotation;
            Vector3 move = target - pos;
            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(move.magnitude / stepSize, Quaternion.Angle(from, turn) / 12f)), 1, 12);
            bool touched = false;
            for (int i = 1; i <= steps; i++)
            {
                pos += move / steps;
                Quaternion rot = Quaternion.Slerp(from, turn, (float)i / steps);
                touched |= PushOut(mine, ref pos, rot);
                held.SetPositionAndRotation(pos, rot);
            }
            if (heldBody != null) { heldBody.position = held.position; heldBody.rotation = held.rotation; }
            if (touched && !Touching)
            {
                Contacts++;
                ToySounds.Play(ToySounds.Cue.Press, held.position, .22f, 2.1f);
            }
            Touching = touched;
        }

        private bool PushOut(Collider mine, ref Vector3 pos, Quaternion rot)
        {
            bool touched = false;
            Transform root = held;
            for (int pass = 0; pass < 4; pass++)
            {
                Vector3 cpos = pos + rot * Vector3.Scale(root.localScale, root.InverseTransformPoint(mine.transform.position));
                Quaternion crot = rot * (Quaternion.Inverse(root.rotation) * mine.transform.rotation);
                float radius = mine.bounds.extents.magnitude + .004f;
                int n = Physics.OverlapSphereNonAlloc(cpos, radius, overlaps, ~0, QueryTriggerInteraction.Ignore);
                bool moved = false;
                for (int i = 0; i < n; i++)
                {
                    var other = overlaps[i];
                    if (!Solid(other, root)) continue;
                    if (!Physics.ComputePenetration(mine, cpos, crot, other, other.transform.position, other.transform.rotation, out Vector3 dir, out float dist) || dist < 1e-5f) continue;
                    Vector3 push = dir * (dist + .0004f);
                    pos += push;
                    cpos += push;
                    moved = touched = true;
                }
                if (!moved) break;
            }
            return touched;
        }

        // The anchor a place is saved against: the surface under it, else the floor (any anchor moves with
        // the room; the one under the thing keeps it on its desk when the desk is re-scanned).
        private MRUKAnchor AnchorUnder(Vector3 point)
        {
            var surface = SurfaceAt(point);
            if (surface?.Anchor != null) return surface.Anchor;
            var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
            return room != null && room.FloorAnchors != null && room.FloorAnchors.Count > 0 ? room.FloorAnchors[0] : null;
        }

        // A saved place back in the room: by the anchor's id (or a name only one anchor has), else the world spot.
        private static bool PlaceFromAnchor(string key, float[] local, float[] rotation, out Vector3 at, out Quaternion rot)
        {
            at = Vector3.zero;
            rot = Quaternion.identity;
            var anchor = FindAnchor(key);
            if (anchor == null || local == null || local.Length < 3) return false;
            at = anchor.transform.TransformPoint(new Vector3(local[0], local[1], local[2]));
            if (rotation != null && rotation.Length == 4) rot = anchor.transform.rotation * new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
            return true;
        }

        // Tests: carry a made thing along a path (the collision steps run every frame of the way), then let go.
        public System.Collections.IEnumerator SimulateDrag(Transform thing, Vector3[] path, float seconds)
        {
            if (thing == null || path == null || path.Length == 0) yield break;
            Grab(null, thing.position, thing); // (not as the mouse: the Editor mouse lets go when no button is down)
            Vector3 start = thing.position;
            // By time (a slow Editor takes fewer, longer steps), at least a step per 2 cm of the way.
            float length = Vector3.Distance(start, path[0]);
            for (int i = 1; i < path.Length; i++) length += Vector3.Distance(path[i - 1], path[i]);
            int minSteps = Mathf.Max(8, Mathf.CeilToInt(length / .02f));
            float began = Time.realtimeSinceStartup;
            for (int f = 1; ; f++)
            {
                // As far as the time allows, but never more than one step per frame.
                float k = Mathf.Min(Mathf.Clamp01((Time.realtimeSinceStartup - began) / Mathf.Max(.01f, seconds)), (float)f / minSteps);
                float t = k * path.Length;
                int i = Mathf.Min(path.Length - 1, (int)t);
                Vector3 a = i == 0 ? start : path[i - 1];
                Vector3 target = Vector3.Lerp(a, path[i], Mathf.Clamp01(t - i));
                if (held != thing) yield break;
                Hold(target, Quaternion.identity); // (grabbed at its own origin: no offset)
                if (k >= 1f) break;
                yield return null;
            }
            LetGo();
        }
    }
}
