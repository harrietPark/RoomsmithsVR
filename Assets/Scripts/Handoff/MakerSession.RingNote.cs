using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // Notes and the ring (2026-11-11, after the 6th Quest try):
    //  - the note written this session (peeled off the pad and spoken) stays in the ring in the middle of the
    //    crew, standing up toward the person: it is what we are making now. It goes up on the wall only when
    //    another idea takes its place (or next time);
    //  - an older note on the wall can be peeled (pinch it near, or far with the hand's ray) and carried: let
    //    go over the ring, that idea is what we talk about now (the one in the ring goes up on the wall);
    //    let go anywhere else, it flies back to its place;
    //  - while a blank note is in hand, talking to the crew ("Maker, can you give me the sketchbook?") is
    //    a request, not the note's words (IsRequest).
    public sealed partial class MakerSession
    {
        public const float RingNoteScale = 1.6f;
        public Transform RingNoteView => ringNote;
        public string RingNoteText => ringData?.text;
        public Transform CarriedNote => carriedNote;
        public int BroughtBack { get; private set; }

        private Transform ringNote, carriedNote;
        private WallNote ringData, carriedData;
        private readonly Dictionary<Transform, WallNote> noteOf = new Dictionary<Transform, WallNote>();
        private Hand noteHand;
        private float noteFar = -1f;
        private Vector3 noteOffset;
        private bool noteOverRing;
        private Vector3? noteTarget; // tests: where the carried note is
        private int carriedSlot = -1;

        // ------------------------------------------------------------ talking while a blank note is in hand

        public bool IsRequest(string text)
        {
            string p = (text ?? "").ToLowerInvariant().Trim();
            if (p.Length == 0) return false;
            bool tools = WantsToDraw(p) || WantsSwatch(p) || AsksAboutMeasuring(p) || p.Contains("notebook") || p.Contains("palette") || p.Contains("undo") ||
                         p.Contains("스케치북") || p.Contains("팔레트") || p.Contains("회의록");
            // "I want to make a lamp" is the note's words, even said to someone.
            if (!tools && Regex.IsMatch(p, @"\b(i want to|i'd like to|i wanna|let's|we should) (make|build|design|create)\b|\bmake (a|an|me|my|some)\b|만들고 싶|만들자|만들래")) return false;
            if (tools) return true;
            if (Regex.IsMatch(p, @"^(hey |ok |okay |um |so )?(spark|maker|fixer)\b") || Regex.IsMatch(p, @"\b(spark|maker|fixer)\s*[,?!]")) return true;
            if (p.EndsWith("?") || Regex.IsMatch(p, @"^(can|could|would|will|what|how|why|where|who|show|give|open|close|let me|help|stop|start over|look|find)\b")) return true;
            return LooksAround(p);
        }

        // ------------------------------------------------------------ the current note in the ring

        // The written note's words (the newest wall note) stand in the ring; the one there before goes up.
        private void PlaceRingNote(WallNote data, Vector3 from, Quaternion fromRotation)
        {
            if (data == null) return;
            if (NoteOnWall) { PlaceCurrentNote(data, from, fromRotation); return; } // F72: on the wall, not in a ring
            if (ringNote != null) SendRingNoteToWall();
            var view = WallView(data);
            view.SetParent(transform, true);
            view.SetPositionAndRotation(from, fromRotation);
            noteOf[view] = data;
            ringNote = view;
            ringData = data;
            StartCoroutine(SettleInRing(view));
        }

        private bool RingPose(out Vector3 at, out Quaternion facing, out Vector3 centre, out float radius)
        {
            if (NoteOnWall) return CurrentNotePose(out at, out facing, out centre, out radius); // F72: the current note on the wall
            at = centre = Vector3.zero;
            facing = Quaternion.identity;
            radius = .1f;
            if (arrival == null || Head == null || !arrival.EnsureRing(out centre, out radius)) return false;
            float half = WallSheet * RingNoteScale * .5f;
            Vector3 spot = centre + RingNoteShift(centre, radius); // F68: off the line from the eyes to Maker
            at = spot + Vector3.up * (half + arrival.RingRest);
            // Facing the person's eyes (the ring is usually below them), upright otherwise.
            Vector3 away = at - Head.position;
            facing = Quaternion.LookRotation(away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward, Vector3.up);
            // The doodle look (2026-11-12): the note lies on the desk inside the drawn circle, leaning back toward
            // the eyes, its lower edge on the desk (the slim ring keeps the note standing above it, as before).
            if (arrival.ringLook != RingLook.Ring)
                for (int i = 0; i < 2; i++)
                {
                    at = spot + Vector3.up * (arrival.RingRest + half * Mathf.Abs((facing * Vector3.up).y) + .004f);
                    away = at - Head.position;
                    facing = Quaternion.LookRotation(away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward, Vector3.up);
                }
            return true;
        }

        // F68 (Quest 2026-11-14: "Maker is always hidden behind the post-it"): the note stood in the middle of the
        // ring facing the eyes, and Maker sits on the crew's arc right behind the middle. It now stands toward the
        // person and to the side away from Maker (the side is chosen once per make, so it never jumps), so the line
        // from the eyes to Maker's face passes beside it.
        public const float RingNoteAside = .62f, RingNoteForward = .3f; // of the ring's radius
        private float ringNoteSide;
        // The arrival's topic note docked in the ring stands at the same spot (WindupsArrival.DockedNotePosition).
        public Vector3 RingNoteOffset(Vector3 centre, float radius) => Head == null ? Vector3.zero : RingNoteShift(centre, radius);

        private Vector3 RingNoteShift(Vector3 centre, float radius)
        {
            Vector3 toUser = Vector3.ProjectOnPlane(Head.position - centre, Vector3.up);
            toUser = toUser.sqrMagnitude > 1e-6f ? toUser.normalized : Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, toUser);
            if (ringNoteSide == 0f)
            {
                var maker = CrewMember(Crew.Maker);
                float makerSide = maker != null ? Vector3.Dot(maker.position - centre, side) : 0f;
                ringNoteSide = makerSide > .005f ? -1f : 1f; // away from Maker (Maker in the middle: the person's right)
            }
            return side * (ringNoteSide * radius * RingNoteAside) + toUser * (radius * RingNoteForward);
        }

        private IEnumerator SettleInRing(Transform view)
        {
            Vector3 fromScale = view.localScale;
            Vector3 from = view.position;
            Quaternion fromRotation = view.rotation;
            if (!RingPose(out var at, out var facing, out var centre, out _)) yield break;
            for (float t = 0f; t < 1f && view != null; t += Time.deltaTime / .55f)
            {
                float e = Mathf.SmoothStep(0f, 1f, t);
                view.position = Vector3.Lerp(from, at, e) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .06f;
                view.rotation = Quaternion.Slerp(fromRotation, facing, e);
                view.localScale = Vector3.Lerp(fromScale, Vector3.one * RingNoteScale, e);
                yield return null;
            }
            if (view == null) yield break;
            view.SetPositionAndRotation(at, facing);
            view.localScale = Vector3.one * RingNoteScale;
            ToySounds.Play(ToySounds.Cue.Press, at, .45f, 1.6f);
            ToyFx.Ring(centre + Vector3.up * .004f, Quaternion.Euler(90f, 0f, 0f), .07f);
            if (view == ringNote) arrival.RingNoteIn();
        }

        // The note in the ring goes up on the wall (another idea takes its place).
        private void SendRingNoteToWall()
        {
            var view = ringNote;
            ringNote = null;
            ringData = null;
            if (view == null) return;
            if (NoteOnWall) StripCurrent(view); // F72: no Rewrite button among the earlier ideas
            if (board == null && !PutUpWall()) { Destroy(view.gameObject); return; }
            if (board == null) { Destroy(view.gameObject); return; }
            view.SetParent(board, true);
            view.localScale = Vector3.one;
            wallViews.RemoveAll(v => v == null);
            wallViews.Add(view);
            while (wallViews.Count > WallShown) { Destroy(wallViews[0].gameObject); wallViews.RemoveAt(0); }
            int count = wallViews.Count;
            for (int i = 0; i < count - 1; i++) StartCoroutine(SlideTo(wallViews[i], SlotLocal(i, count)));
            StartCoroutine(FlyToWall(view, SlotLocal(count - 1, count), Quaternion.Euler(0f, 0f, Random.Range(-4f, 4f))));
            arrival.PutNoteOnPad(PadPose.position, PadPose.rotation, BoardScale);
        }

        // ------------------------------------------------------------ peeling an older note off the wall

        private bool WallNotePinch(Hand hand, Vector3 point, Ray? ray)
        {
            if (carriedNote != null || board == null) return false;
            Transform pick = null;
            float far = -1f;
            foreach (var v in WallCarryables())
            {
                if (v == null) continue;
                if (Vector3.Distance(point, v.position) < WallSheet * v.lossyScale.x * .6f + .015f) { pick = v; break; }
            }
            if (pick == null && ray.HasValue)
            {
                float best = float.MaxValue;
                foreach (var v in WallCarryables())
                {
                    if (v == null) continue;
                    var plane = new Plane(v.forward, v.position);
                    if (!plane.Raycast(ray.Value, out float d) || d < .2f || d > 3.5f || d > best) continue;
                    Vector3 local = v.InverseTransformPoint(ray.Value.GetPoint(d));
                    if (Mathf.Abs(local.x) > WallSheet * .65f || Mathf.Abs(local.y) > WallSheet * .65f) continue;
                    best = d;
                    pick = v;
                    far = d;
                }
            }
            if (pick == null) return false;
            CarryNote(pick, hand, point, far);
            return true;
        }

        private void CarryNote(Transform view, Hand hand, Vector3 point, float far)
        {
            carriedNote = view;
            carriedData = noteOf.TryGetValue(view, out var data) ? data : null;
            carriedSlot = wallViews.IndexOf(view);
            looseNotes.Remove(view); // F72
            noteHand = hand;
            noteFar = far;
            view.SetParent(transform, true);
            noteOffset = view.position - point;
            ToySounds.Play(ToySounds.Cue.Peel, view.position, .55f, 1.3f);
            ToyFx.Puff(view.position, .01f, 3);
            Heard();
            Talk.Add("Person peeled an earlier note off the wall: " + (carriedData?.text ?? "?"));
        }

        private void NoteCarryFrame()
        {
            if (carriedNote == null) return;
            if (NoteCrumpleGesture()) return; // F98: a fist, or the other hand's pinch, crumples it
            bool live = noteTarget.HasValue || (noteHand != null && noteHand.isActiveAndEnabled && HandIO.Connected(noteHand) && (HandIO.Pinching(noteHand) || HandFist(noteHand)));
            if (!live) { DropCarriedNote(); return; }
            Vector3 target;
            if (noteTarget.HasValue) target = noteTarget.Value;
            else if (noteFar > 0f && HandIO.Pointer(noteHand, out Pose pointer))
            {
                target = pointer.position + pointer.forward * noteFar;
                // Aimed at the ring: the note comes over it (the ring is nearer than the wall).
                if (carriedNote != ringNote && RingPose(out var at, out var noteFacing, out var centre, out float radius))
                {
                    var ray = new Ray(pointer.position, pointer.forward);
                    float along = Vector3.Dot(centre - ray.origin, ray.direction);
                    if (along > .1f && Vector3.Distance(ray.GetPoint(along), centre) < radius + .12f) target = NoteOnWall ? at - noteFacing * Vector3.forward * .04f : at + Vector3.up * .05f;
                }
            }
            else target = PinchPoint(noteHand) + noteOffset;
            carriedNote.position = Vector3.Lerp(carriedNote.position, target, 1f - Mathf.Exp(-Time.deltaTime * 18f));
            Vector3 away = Head != null ? carriedNote.position - Head.position : Vector3.forward;
            if (away.sqrMagnitude > 1e-4f) carriedNote.rotation = Quaternion.Slerp(carriedNote.rotation, Quaternion.LookRotation(away.normalized, Vector3.up), 1f - Mathf.Exp(-Time.deltaTime * 10f));
            carriedNote.localScale = Vector3.Lerp(carriedNote.localScale, Vector3.one * (NoteOnWall ? BoardScale * (carriedNote == ringNote ? CurrentNoteScale : 1.15f) : RingNoteScale), 1f - Mathf.Exp(-Time.deltaTime * 6f));
            bool over = OverRing(carriedNote.position);
            if (over && !noteOverRing) ToySounds.Play(ToySounds.Cue.Pop, carriedNote.position, .3f, 1.8f);
            noteOverRing = over;
        }

        private bool OverRing(Vector3 at)
        {
            if (NoteOnWall) return NearCurrentNote(at); // F72: on the current note on the wall
            if (!RingPose(out _, out _, out var centre, out float radius)) return false;
            float up = at.y - centre.y;
            return Vector3.ProjectOnPlane(at - centre, Vector3.up).magnitude < radius + .15f && up > -.1f && up < .6f;
        }

        private void DropCarriedNote()
        {
            var view = carriedNote;
            var data = carriedData;
            carriedNote = null;
            carriedData = null;
            noteHand = null;
            noteTarget = null;
            noteOverRing = false;
            if (view == null) return;
            wallViews.Remove(view);
            // (F97: the current note itself is never "let go on the current note": that began its idea again.)
            if (data != null && view != ringNote && OverRing(view.position)) { BringBackIdea(view, data); return; }
            if (NoteOnWall) { LeaveNoteWhereLetGo(view, data); return; } // F72: grab & release: it stays where it was let go
            // Anywhere else: back to its place on the wall.
            if (board == null) { Destroy(view.gameObject); return; }
            view.SetParent(board, true);
            view.localScale = Vector3.one;
            int slot = Mathf.Clamp(carriedSlot < 0 ? wallViews.Count : carriedSlot, 0, wallViews.Count);
            wallViews.Insert(slot, view);
            int count = wallViews.Count;
            for (int i = 0; i < count; i++)
                if (wallViews[i] != view) StartCoroutine(SlideTo(wallViews[i], SlotLocal(i, count)));
            StartCoroutine(FlyToWall(view, SlotLocal(slot, count), Quaternion.Euler(0f, 0f, Random.Range(-4f, 4f))));
        }

        // An older idea in the ring: it is what we make now.
        private void BringBackIdea(Transform view, WallNote data)
        {
            BroughtBack++;
            if (ringNote != null) SendRingNoteToWall();
            ringNote = view;
            ringData = data;
            // It becomes the newest note (so it is not added twice when the make begins).
            Wall.notes.Remove(data);
            Wall.notes.Add(data);
            SaveWall();
            // (PutAside stops the session's coroutines: the new make begins first, then the note settles.)
            PutAside();
            Begin(data.text);
            Talk.Add("Person brought back an earlier idea: " + data.text);
            int count = wallViews.Count;
            for (int i = 0; i < count; i++) StartCoroutine(SlideTo(wallViews[i], SlotLocal(i, count)));
            if (board != null) arrival.PutNoteOnPad(PadPose.position, PadPose.rotation, BoardScale);
            if (NoteOnWall) MakeCurrent(view); else StartCoroutine(SettleInRing(view)); // F72
            ToyFx.Confetti(view.position + Vector3.up * .05f, 10);
            Ack(Crew.Maker, "Back to this one!");
        }

        // ------------------------------------------------------------ tests

        public bool SimulatePeelWallNote(int index)
        {
            var views = wallViews.Where(v => v != null).ToList();
            if (index < 0 || index >= views.Count) return false;
            CarryNote(views[index], null, views[index].position, -1f);
            noteTarget = views[index].position;
            return true;
        }

        public void SimulateCarryNote(Vector3 to) { if (carriedNote != null) noteTarget = to; }
        public void SimulateReleaseNote() { if (carriedNote != null) DropCarriedNote(); }
        public Vector3 RingCentre => RingPose(out _, out _, out var centre, out _) ? centre : Vector3.zero;
        // Tests: the ring's note goes straight to its place for the ring's current look.
        public void SimulateResettleRingNote()
        {
            if (ringNote == null || !RingPose(out var at, out var facing, out _, out _)) return;
            ringNote.SetPositionAndRotation(at, facing);
            arrival.RingNoteIn();
        }
    }
}
