using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Meta.XR.MRUtilityKit;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // The idea wall (2026-11-11). Every idea the person starts (the words on their sticky note, a spoken
    // need, a picked suggestion) is kept in maker_wall.json. When they come back, those notes are stuck up
    // in front of them, newest last, with a pad of fresh notes beside them:
    //  - on the nearest real wall (MRUK geometry, within 2.2 m), beside the moodboard if there is one;
    //  - else lying on the nearest desk or shelf by the person (top edge away from them);
    //  - else floating to the person's right.
    // The pad's top sheet is the arrival's topic note. Peeling it off (near grab or far pinch) and saying
    // something starts a new make (`NewIdea`); let go, the written note flies up beside the others and a
    // fresh one waits on the pad. The board's place is kept against its MRUK anchor, so it comes back to
    // the same spot.
    public sealed partial class MakerSession
    {
        // F30: `who` wrote it (a teammate's idea from the meeting: its colour and face) and what `kind` of thing.
        // F72: pos/turn = where the person put it (board-local); null = in the grid.
        [Serializable] public sealed class WallNote { public string text, at, variant, who, kind, projectId; public float[] pos, turn; }
        [Serializable]
        private sealed class WallData
        {
            public List<WallNote> notes = new List<WallNote>();
            public string place, anchor;
            public float[] local, rotation;
        }

        public static string WallPath => Path.Combine(Application.persistentDataPath, "maker_wall.json");
        public const int WallShown = 6, WallColumns = 3;
        public const float WallSheet = .072f, WallStep = .086f, WallReach = 2.2f;
        public static string WallPlaceOverride;   // tests: "surface" or "air" skip the places before it

        public IReadOnlyList<WallNote> WallNotes => Wall.notes;
        public string WallPlace => board != null ? Wall.place : null;
        public Transform WallBoard => board;
        public int WallSheets => wallViews.Count(v => v != null);
        public Pose PadPose => board != null ? new Pose(board.TransformPoint(PadLocal() + Vector3.back * PadLift), board.rotation) : new Pose(Vector3.zero, Quaternion.identity);
        private const float PadLift = .035f;   // the pad's paper stack stands out of the wall

        private WallData wall;
        private Transform board;
        private readonly List<Transform> wallViews = new List<Transform>();

        private WallData Wall
        {
            get
            {
                if (wall != null) return wall;
                bool saved = File.Exists(WallPath);
                try { wall = saved ? JsonConvert.DeserializeObject<WallData>(File.ReadAllText(WallPath)) : null; }
                catch (Exception exception) { Debug.LogWarning("[Windups Wall] " + exception.Message); }
                wall ??= new WallData();
                if (!saved) SeedWallFromNotebook();
                return wall;
            }
        }

        // Made things before the wall existed: their briefs in Maker's notebook become the first notes.
        private void SeedWallFromNotebook()
        {
            LoadNotes();
            foreach (var page in Notes.Where(n => n.kind == "brief" && !string.IsNullOrWhiteSpace(n.text)))
            {
                string text = MakerRoles.Clean(page.text).Trim();
                if (text.Length > 90) text = text.Substring(0, 87) + "...";
                if (wall.notes.Count > 0 && string.Equals(wall.notes[wall.notes.Count - 1].text, text, StringComparison.OrdinalIgnoreCase)) continue;
                string at = page.at ?? "";
                int comma = at.IndexOf(',');
                wall.notes.Add(new WallNote { text = text, at = comma > 0 ? at.Substring(0, comma) : at, variant = WindupsPostIt.Variants[wall.notes.Count % 3] });
            }
            if (wall.notes.Count > 0) SaveWall();
        }

        private void SaveWall()
        {
            try { File.WriteAllText(WallPath, JsonConvert.SerializeObject(Wall, Formatting.Indented)); }
            catch (Exception exception) { Debug.LogWarning("[Windups Wall] " + exception.Message); }
        }

        public static void ForgetWall()
        {
            try { if (File.Exists(WallPath)) File.Delete(WallPath); } catch { }
        }

        // A new idea goes on the list (the same words twice in a row count once).
        private void RememberWallNote(string text, string who = null, string kind = null)
        {
            text = MakerRoles.Clean(text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 90) text = text.Substring(0, 87) + "...";
            var notes = Wall.notes;
            if (notes.Count > 0 && string.Equals(notes[notes.Count - 1].text, text, StringComparison.OrdinalIgnoreCase)) return;
            notes.Add(new WallNote { text = text, at = DateTime.Now.ToString("MMM d"), variant = WindupsPostIt.Variants[UnityEngine.Random.Range(0, 3)], who = who, kind = kind });
            SaveWall();
        }

        // ------------------------------------------------------------ putting it up

        // On the way back in: the notes go up and a fresh one waits on the pad.
        public bool PutUpWall()
        {
            if (board != null) return true;
            if (Wall.notes.Count == 0 || Head == null) return false;
            if (!RestoreBoardPose(out var pose) && !FindBoardPose(out pose)) return false;
            board = new GameObject("Idea Wall").transform;
            board.SetParent(transform, false);
            board.SetPositionAndRotation(pose.position, pose.rotation);
            // Readable from where the person is: real post-it size within arm's length, bigger further away.
            board.localScale = Vector3.one * WallScale;
            // The note in the ring (this session's idea) is not on the wall.
            var up = Wall.notes.Where(n => n != ringData).ToList();
            // F72: notes the person moved stay where they were put; the others are in the grid.
            var grid = NoteOnWall ? up.Where(n => n.pos == null || n.pos.Length != 3).ToList() : up;
            foreach (var note in grid) wallViews.Add(WallView(note));
            if (NoteOnWall) RestoreLooseNotes(up);
            LayOutWall(true);
            arrival.PutNoteOnPad(PadPose.position, PadPose.rotation, BoardScale);
            Debug.Log("[Windups Wall] " + WallSheets + " note(s) on the " + Wall.place + " at " + pose.position.ToString("F2"));
            return true;
        }

        public float BoardScale => board != null ? board.localScale.x : 1f;
        // F104 (Quest 2026-11-16, the user: "make the post-it pad and the post-its a little smaller"): the whole board (notes,
        // the pad, the current note and what is pinned around it, keeping their layout) is WallShrink of its old size.
        public static float WallShrink = .85f;
        public float WallScale => board == null || Head == null ? 1f : Mathf.Clamp(Vector3.Distance(board.position, Head.position) / .9f, 1f, 1.8f) * WallShrink;

        public void TakeDownWall()
        {
            if (board != null) Destroy(board.gameObject);
            board = null;
            wallViews.Clear();
            looseNotes.Clear(); // F72 (the current note is on the board too)
            if (NoteOnWall) { StripCurrent(null); ringNote = null; ringData = null; }
        }

        private bool RestoreBoardPose(out Pose pose)
        {
            pose = default;
            var data = Wall;
            if (!string.IsNullOrEmpty(WallPlaceOverride) || data.place == "air" || (NoteOnWall && data.place == "surface") || string.IsNullOrEmpty(data.anchor) || data.local == null || data.rotation == null) return false;
            var anchor = FindAnchor(data.anchor);
            if (anchor == null) return false;
            pose = new Pose(anchor.transform.TransformPoint(new Vector3(data.local[0], data.local[1], data.local[2])),
                anchor.transform.rotation * new Quaternion(data.rotation[0], data.rotation[1], data.rotation[2], data.rotation[3]));
            // Only if the person can still reach it with their eyes (same room, not behind a wall far off).
            return Vector3.Distance(pose.position, Head.position) < WallReach + .6f;
        }

        private bool FindBoardPose(out Pose pose)
        {
            string skip = WallPlaceOverride ?? "";
            MRUKAnchor anchor = null;
            pose = default;
            string place = null;
            if (skip.Length == 0 && WallSpot(out var at, out var normal, out anchor))
            {
                pose = new Pose(at + normal * .004f, Quaternion.LookRotation(-normal, Vector3.up));
                place = "wall";
            }
            else if (!NoteOnWall && skip != "air" && SurfaceSpot(out at, out anchor)) // F72: never lying on a desk
            {
                Vector3 away = Vector3.ProjectOnPlane(at - Head.position, Vector3.up);
                if (away.sqrMagnitude < 1e-4f) away = Vector3.ProjectOnPlane(Head.forward, Vector3.up);
                pose = new Pose(at + Vector3.up * .003f, Quaternion.LookRotation(Vector3.down, away.normalized));
                place = "surface";
            }
            else if (NoteOnWall && AirBoardPose(out pose)) place = "air"; // F72: up and to the left of Fixer, above the heads
            else
            {
                Vector3 forward = Vector3.ProjectOnPlane(Head.forward, Vector3.up).normalized;
                if (forward.sqrMagnitude < .01f) forward = Vector3.forward;
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                Vector3 position = Head.position + forward * .42f + right * .42f - Vector3.up * .1f;
                Vector3 look = position - Head.position;
                pose = new Pose(position, Quaternion.LookRotation(Vector3.ProjectOnPlane(look, Vector3.up).normalized, Vector3.up));
                place = "air";
            }
            var data = Wall;
            data.place = place;
            data.anchor = anchor != null ? AnchorKey(anchor) : null;
            data.local = data.rotation = null;
            if (anchor != null)
            {
                Vector3 local = anchor.transform.InverseTransformPoint(pose.position);
                Quaternion turn = Quaternion.Inverse(anchor.transform.rotation) * pose.rotation;
                data.local = new[] { local.x, local.y, local.z };
                data.rotation = new[] { turn.x, turn.y, turn.z, turn.w };
            }
            SaveWall();
            return true;
        }

        // An anchor's lasting id (its scene UUID; several walls share the name WALL_FACE).
        private static string AnchorKey(MRUKAnchor anchor)
        {
            var id = anchor.Anchor.Uuid;
            return id != Guid.Empty ? id.ToString() : anchor.name;
        }

        private static MRUKAnchor FindAnchor(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
            if (room == null) return null;
            var match = room.Anchors.FirstOrDefault(a => a != null && AnchorKey(a) == key);
            if (match != null) return match;
            // Saved before ids were used: only a name that is unique in the room is trusted.
            var named = room.Anchors.Where(a => a != null && a.name == key).ToList();
            return named.Count == 1 ? named[0] : null;
        }

        // The nearest wall in front (as the moodboard finds it), within reach of the eyes. With a moodboard
        // on it, the notes go beside the pictures (left, right, then below), where the wall really is.
        private bool WallSpot(out Vector3 at, out Vector3 normal, out MRUKAnchor anchor)
        {
            at = normal = Vector3.zero;
            anchor = null;
            LoadPins();
            if (!FindWall() || Vector3.Distance(Vector3.ProjectOnPlane(wallPoint - Head.position, Vector3.up), Vector3.zero) > WallReach) return false;
            normal = wallNormal;
            anchor = wallAnchor;
            Vector3 right = Vector3.Cross(Vector3.up, normal) * -1f;   // the reader's right on this wall
            float rows = Mathf.Ceil(Wall.notes.Count + 1 / (float)WallColumns);
            Vector3 centre = wallPoint - Vector3.up * .08f;
            var tries = new List<Vector3>();
            if (Pins.Count > 0 || PinViews.Count > 0)
            {
                tries.Add(centre - right * .58f);
                tries.Add(centre + right * .58f);
                tries.Add(centre - Vector3.up * (.3f + rows * WallStep * .5f));
            }
            tries.Add(centre);
            int mask = arrival.handoff.meeting.environment.GeometryMask;
            foreach (var spot in tries)
            {
                // The wall is really there (not a doorway), and nothing stands in front of it.
                var ray = new Ray(Head.position, spot - Head.position);
                if (!Physics.Raycast(ray, out var hit, 5f, mask, QueryTriggerInteraction.Ignore) || Mathf.Abs(hit.normal.y) > .3f ||
                    Vector3.Distance(hit.point, spot) > .05f) continue;
                at = hit.point;
                normal = Vector3.ProjectOnPlane(hit.normal, Vector3.up).normalized;
                anchor = hit.collider.GetComponentInParent<MRUKAnchor>() ?? wallAnchor;
                return true;
            }
            return false;
        }

        // A free spot on a desk or shelf next to the person (not where the crew or the things made stand).
        private bool SurfaceSpot(out Vector3 at, out MRUKAnchor anchor)
        {
            at = Vector3.zero;
            anchor = null;
            int mask = arrival.handoff.meeting.environment.GeometryMask;
            Vector3 forward = Vector3.ProjectOnPlane(Head.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .01f) forward = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            var busy = cast.Values.Where(c => c != null).Select(c => c.transform.position).ToList();
            busy.AddRange(Occupied().Select(o => o.at));
            if (arrival.MicShown) busy.Add(arrival.MicPosition - Vector3.up * WindupsArrival.MicBase);
            foreach (var (ahead, side) in new[] { (.42f, .32f), (.5f, .18f), (.38f, .48f), (.55f, 0f), (.45f, -.3f), (.3f, .55f) })
            {
                Vector3 above = Head.position + forward * ahead + right * side;
                if (!Physics.Raycast(new Ray(above, Vector3.down), out var hit, 1.6f, mask, QueryTriggerInteraction.Ignore) || hit.normal.y < .9f) continue;
                if (hit.point.y < .4f || hit.point.y > Head.position.y - .25f) continue;
                Vector3 p = hit.point;
                if (busy.Any(b => Vector3.ProjectOnPlane(b - p, Vector3.up).magnitude < .18f)) continue;
                at = p;
                anchor = hit.collider.GetComponentInParent<MRUKAnchor>();
                return true;
            }
            return false;
        }

        // What stands on the desks around the person (for placing the mic and the board): things made,
        // the real object being measured, what Spark saw, the closed notebook, the drawing page and the
        // board itself when it lies on a desk. (where it stands, how far it reaches sideways)
        public List<(Vector3 at, float radius)> Occupied()
        {
            var spots = new List<(Vector3, float)>();
            foreach (var t in MadeThings())
            {
                var renderer = t.GetComponentInChildren<MeshRenderer>();
                if (renderer == null) continue;
                var b = renderer.bounds;
                spots.Add((new Vector3(b.center.x, b.min.y, b.center.z), Mathf.Max(b.extents.x, b.extents.z) + .03f));
            }
            // F40: v2's see-through twin takes its place on the desk too.
            if (draftMesh != null)
            {
                var b = draftMesh.GetComponent<MeshRenderer>().bounds;
                spots.Add((new Vector3(b.center.x, b.min.y, b.center.z), Mathf.Max(b.extents.x, b.extents.z) + .03f));
            }
            if (HasSpot) spots.Add((MugSpot, (HasMeasure ? Measured * .0005f : .045f) + .01f));
            if (Target != null && Target.onSurface) spots.Add((Target.basePoint, Target.width * .5f + .01f));
            if (Eyes != null)
                foreach (var f in Eyes.LastFound)
                    if (f != null && f.onSurface) spots.Add((f.basePoint, Mathf.Clamp(f.width * .5f, .02f, .2f) + .01f));
            if (notebookClosed != null && notebookClosed.gameObject.activeInHierarchy) spots.Add((notebookClosed.position, .1f));
            if (paper != null && paper.gameObject.activeInHierarchy) spots.Add((paper.position, PaperSize * .75f));
            if (board != null && Wall.place == "surface")
                spots.Add((board.position, (WallColumns + 1) * WallStep * .5f * board.localScale.x + .02f));
            // F51: the show-me circle, the idea notes, the ring's note and the flags standing on the desk take
            // their places too (the mic stood 5 cm inside the circle).
            if (ShowCircleShown && FrameLying)
            {
                spots.Add((ShowCircleCentre, ShowFootRadius)); // F56: Spark's frame on the desk
                // F79: the circle is its mean half side; a long frame's corners and its size handle reach past it
                // (the mic stood 3.7 cm from the handle of a 20 x 12 cm frame).
                for (int i = 0; i < 4; i++) spots.Add((FrameCorner(i), FrameCornerFoot));
                spots.Add((FrameHandlePoint, FrameHandleFoot));
            }
            if (TrashShown) spots.Add((trashBasket.position, .04f));
            foreach (var idea in Ideas)
                if (idea != null && idea.root != null && idea.placed) spots.Add((idea.root.position - Vector3.up * NoteSize * .5f, NoteSize * .5f + .01f));
            if (ringNote != null && !NoteOnWall && ringNote.gameObject.activeInHierarchy && ringNote != carriedNote) spots.Add((ringNote.position, .045f));
            foreach (var flag in Flags)
                if (flag != null && flag.root != null && flag.root.gameObject.activeInHierarchy && (flag.place == FlagPlace.Offered || flag.place == FlagPlace.Parked))
                    spots.Add((flag.root.position, .02f));
            return spots;
        }

        // ------------------------------------------------------------ the board

        // Rows of three, newest last; the pad sits right after the top row, and the top row (with the pad)
        // is centred on the board's spot.
        private static float ColumnX(int col, int count)
        {
            int across = Mathf.Min(Mathf.Max(1, count), WallColumns) + 1;
            return (col - (across - 1) * .5f) * WallStep;
        }

        private Vector3 SlotLocal(int i, int count)
        {
            if (NoteOnWall) return GridSlot(i, count); // F72: left of the pad; the current note at the board's origin
            int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)WallColumns));
            int col = i % WallColumns, row = i / WallColumns;
            return new Vector3(ColumnX(col, count), (rows - 1) * WallStep * .5f - row * WallStep, 0f);
        }

        private Vector3 PadLocal()
        {
            if (NoteOnWall) return PadSlot();
            int count = Mathf.Max(1, wallViews.Count);
            int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)WallColumns));
            return new Vector3(ColumnX(Mathf.Min(count, WallColumns), count) + .006f, (rows - 1) * WallStep * .5f, 0f);
        }

        private void LayOutWall(bool snap)
        {
            wallViews.RemoveAll(v => v == null);
            for (int i = 0; i < wallViews.Count; i++)
            {
                var slot = SlotLocal(i, wallViews.Count);
                if (snap) wallViews[i].localPosition = slot;
                else StartCoroutine(SlideTo(wallViews[i], slot));
            }
        }

        private static IEnumerator SlideTo(Transform view, Vector3 local)
        {
            Vector3 from = view.localPosition;
            for (float t = 0f; t < 1f && view != null; t += Time.deltaTime / .35f)
            {
                view.localPosition = Vector3.Lerp(from, local, Mathf.SmoothStep(0f, 1f, t));
                yield return null;
            }
            if (view != null) view.localPosition = local;
        }

        // One stuck-up note: the designed post-it with the words on it, a little crooked.
        private Transform WallView(WallNote note)
        {
            var root = new GameObject("Wall Note " + note.text).transform;
            root.SetParent(board, false);
            var model = Resources.Load<GameObject>("WindupsPostIt_" + (string.IsNullOrEmpty(note.variant) ? "A" : note.variant));
            if (model != null)
            {
                var look = Instantiate(model, root, false).transform;
                look.name = "Sheet";
                look.localRotation = Quaternion.Euler(-90f, 0f, 0f); // the sheet's face (+Y) toward the reader (-Z)
                float s = WallSheet / WindupsPostIt.ModelSize;
                look.localScale = new Vector3(s, Mathf.Min(2f, s), s);
            }
            var canvas = new GameObject("Words", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(root, false);
            rect.sizeDelta = new Vector2(WallSheet * 1000f, WallSheet * 1000f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, -.002f, -.004f);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var words = HatIdeaCard.Label(rect, font, 5f, 8f, WallSheet * 1000f - 10f, WallSheet * 1000f - 16f, 5.5f, 11f,
                new Color(.2f, .19f, .22f), FontStyles.Normal, TextAlignmentOptions.Center);
            words.text = note.text;
            var date = HatIdeaCard.Label(rect, font, 6f, WallSheet * 1000f - 11f, WallSheet * 1000f - 12f, 8f, 4f, 4f,
                new Color(.45f, .42f, .38f), FontStyles.Normal, TextAlignmentOptions.Right);
            date.text = note.at ?? "";
            root.localRotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(-4f, 4f));
            DressWallNote(root, note, words, WallSheet * 500f - 2f - 8f); // F30: a teammate's idea keeps its colour and face
            noteOf[root] = note;
            RefreshFinishedNote(root, note);
            return root;
        }

        // ------------------------------------------------------------ peeling a note off the pad

        // The person peeled a fresh note: the crew waits for the new idea.
        public void PadNotePeeled()
        {
            Heard();
            Hush();
            if (WorkArchived) // F98: after a finished work, taking a post-it is "let's make something"
            {
                CloseQuestion("a note peeled");
                makeAnotherAgainAt = -1f;
                Ack(Crew.Maker, "Let's make it! Tell me your idea.");
            }
            else Ack(Crew.Maker, "A new idea? Hold the mic and tell me!");
            Talk.Add("The person peeled a new note off the pad.");
        }

        // The words on the peeled note: what we make now. Whatever was going on is put aside (a finished
        // make stays where it is), as with "start over", but the note is already in hand.
        public void NewIdea(string need)
        {
            need = need?.Trim() ?? "";
            if (need.Length == 0) return;
            PutAside();
            Begin(need);
            Ack(Crew.Spark, "Ooh, a new one!");
        }

        // The written note is let go: it stands in the ring (what we make now; it goes up on the wall when
        // another idea takes its place), and a fresh one waits on the pad.
        public void StickWrittenNote(Transform note)
        {
            var data = Wall.notes.Count > 0 ? Wall.notes[Wall.notes.Count - 1] : null;
            if (data == null) return;
            PlaceRingNote(data, note.position, note.rotation);
            if (board != null || PutUpWall()) arrival.PutNoteOnPad(PadPose.position, PadPose.rotation, BoardScale);
            else arrival.PutNoteOnPad(note.position, note.rotation, 1f);
            ToySounds.Play(ToySounds.Cue.Paper, note.position, .7f, 1.2f);
        }

        private IEnumerator FlyToWall(Transform view, Vector3 slot, Quaternion turn)
        {
            Vector3 from = board.InverseTransformPoint(view.position);
            Quaternion fromTurn = Quaternion.Inverse(board.rotation) * view.rotation;
            for (float t = 0f; t < 1f && view != null; t += Time.deltaTime / .6f)
            {
                float e = Mathf.SmoothStep(0f, 1f, t);
                view.localPosition = Vector3.Lerp(from, slot, e) + Vector3.back * Mathf.Sin(t * Mathf.PI) * .08f;
                view.localRotation = Quaternion.Slerp(fromTurn, turn, e);
                yield return null;
            }
            if (view == null) yield break;
            view.localPosition = slot;
            view.localRotation = turn;
            ToySounds.Play(ToySounds.Cue.Press, view.position, .45f, 1.8f);
            ToyFx.Puff(view.position, .008f, 3);
        }
    }
}
