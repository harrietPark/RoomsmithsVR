using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F72 (2026-11-14, the user: "no ring: when I confirm, the post-it goes straight onto a wall near me, or with no wall
    // up in the air to the upper left of Black (Fixer), not over it; a Rewrite button inside the post-it, lower left:
    // pressing it just clears the words, then I press the mic and say it; everything on the wall is grab & release; the
    // pictures on the wall have an x: pressed, the picture is crumpled into Black's basket, or I crumple it myself"):
    //  - NoteOnWall (default true; false keeps the F35 ring for the older checks). This make's note goes on the idea
    //    wall's board: the current note at the board's origin, a little bigger (CurrentNoteScale), the pad of fresh notes
    //    and the earlier ideas to its left, the shape cards under it, pictures above it. The board goes on the nearest
    //    real wall (Wall.cs WallSpot), else in the air up and to the person's left of Fixer, above every head
    //    (AirBoardPose); never lying on a desk any more.
    //  - Before the make begins the note has two buttons: Rewrite (lower left) and the green check (lower right, the
    //    same as "I'm done"). On the wall the current note keeps Rewrite. Pressed, the words just go (no eraser); the mic
    //    is not started: the next words said (holding the mic) are the note. Pressed again, or "never mind", the old
    //    words come back. Rewritten before any work, the same note gets the new words and the make starts again from
    //    them; after work began, the old note joins the earlier ideas and the new one is the current note.
    //  - Every note and picture on the wall is carried grab & release. Let go on the current note, an earlier idea is
    //    what we make now and a picture is a reference (it flies back); let go within StickToWall of the board or wall it
    //    came from it sticks there, else it stays in the air facing the person. Places are kept (WallNote.pos/turn,
    //    Pin.world/turn/scale).
    //  - A picture on the wall and a shape card on the note have an x: pressed, it is crumpled and flies into Fixer's
    //    basket. Carried, a fist or the other hand's pinch crumples it; the ball let go by the basket or thrown at it goes
    //    in, a miss lies where it fell and can be picked up again.
    public sealed partial class MakerSession
    {
        public static bool NoteOnWall = true;
        public const float CurrentNoteScale = 1.4f, NoteButtonRadius = .0085f, XButtonRadius = .0075f, StickToWall = .15f;
        public const string RewriteHint = "Say it again...";
        private const float CurrentWordsRoom = 42f; // mm of the current note left for its words, above the Rewrite button

        private static float CurrentHalf => WallSheet * CurrentNoteScale * .5f;
        private static float PadX => -(CurrentHalf + .022f + WallSheet * .5f);
        private static float TopRowY => CurrentHalf - WallSheet * .5f;

        public bool NoteRewriting { get; private set; }
        public int Rewrites { get; private set; }
        public int PinsTrashed { get; private set; }
        public int PinsCrumpled { get; private set; }
        public int CardsCrossed { get; private set; }
        public int LooseNoteCount => looseNotes.Count;
        public Transform CurrentNoteView => NoteOnWall ? ringNote : null;
        public string CurrentWordsText => currentWords != null ? currentWords.text : null;
        public Transform RewriteButtonView => rewriteButton?.root;
        public string RewriteLabel => rewriteButton?.label != null ? rewriteButton.label.text : null;
        public Transform TopicDoneView => topicDone?.root;
        public Transform TopicRewriteView => topicRewrite?.root;
        public bool TopicButtonsShown => topicDone != null && topicDone.root != null && topicDone.root.gameObject.activeInHierarchy;
        public bool CarryingPin => pinCarry != null;
        public PaperBall CarriedPinBall => pinCarry?.ball;
        public int LoosePinBalls => looseBalls.Count;
        private bool PinTrashWanted => (pinCarry != null && pinCarry.ball != null) || ballsFlying > 0 || AnyBallMoving();

        // A round button on a note or picture: a coloured rim, a raised face that lights when pressed, an icon.
        private sealed class RoundButton
        {
            public Transform root, cap, icon;
            public MeshFilter face;
            public Mesh normal, lit;
            public TextMeshProUGUI label;
            public float radius, spin, pressedAt = -10f, near = float.MaxValue;
            public Vector3 baseScale = Vector3.one;
            public readonly FingerPress press = new FingerPress();
        }

        // A picture taken off the wall (or the paper ball it was crumpled into).
        private sealed class PinCarry
        {
            public Pin pin;
            public Transform view;
            public PaperBall ball;
            public Hand hand;
            public float far = -1f, fistSince = -1f;
            public Vector3 offset, homePos;
            public Quaternion homeRot;
            public Vector3? target; // checks: where it is carried
            public bool overNote;
        }

        private readonly List<Transform> looseNotes = new List<Transform>();
        private readonly Dictionary<Transform, RoundButton> pinX = new Dictionary<Transform, RoundButton>();
        private readonly Dictionary<ShapeCard, RoundButton> cardX = new Dictionary<ShapeCard, RoundButton>();
        private readonly Dictionary<Hand, bool> topicPinchWas = new Dictionary<Hand, bool>();
        private readonly List<PaperBall> looseBalls = new List<PaperBall>();
        private readonly List<(Vector3 at, float time)> pinPath = new List<(Vector3, float)>();
        private RoundButton rewriteButton, topicRewrite, topicDone;
        private TextMeshProUGUI currentWords;
        private float currentWordsHeight = -1f, wiggleUntil;
        private string rewriteOld;
        private PinCarry pinCarry;
        private int ballsFlying;

        // ------------------------------------------------------------ the board's layout (board-local, x = the reader's right)

        // The earlier ideas in rows of three to the left of the pad, the newest nearest it.
        private static Vector3 GridSlot(int i, int count)
        {
            int k = Mathf.Max(0, count - 1 - i), col = k % WallColumns, row = k / WallColumns;
            return new Vector3(PadX - (col + 1) * WallStep, TopRowY - row * WallStep, 0f);
        }

        private static Vector3 PadSlot() => new Vector3(PadX, TopRowY, 0f);

        // Up and to the person's left of Fixer: the note's right edge left of Fixer's middle, the lowest row of cards
        // under it above every head.
        private bool AirBoardPose(out Pose pose)
        {
            pose = default;
            var fixer = CrewMember(Crew.Fixer);
            if (fixer == null || Head == null) return false;
            Vector3 toFixer = Vector3.ProjectOnPlane(fixer.position - Head.position, Vector3.up);
            if (toFixer.sqrMagnitude < 1e-4f) return false;
            toFixer.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, toFixer); // the person's right
            float heads = fixer.position.y + .16f;
            foreach (var c in cast.Values)
                if (c != null && c.movement != null && Mathf.Abs(c.movement.transform.position.y - fixer.position.y) < .3f) heads = Mathf.Max(heads, c.movement.transform.position.y + .16f);
            float s = 1f;
            Vector3 at = fixer.position;
            for (int k = 0; k < 3; k++)
            {
                at = new Vector3(fixer.position.x, heads, fixer.position.z) + Vector3.up * (.04f + (CurrentHalf + .08f) * s) - right * ((CurrentHalf + .05f) * s);
                s = Mathf.Clamp(Vector3.Distance(at, Head.position) / .9f, 1f, 1.8f);
            }
            Vector3 away = Vector3.ProjectOnPlane(at - Head.position, Vector3.up);
            pose = new Pose(at, Quaternion.LookRotation(away.sqrMagnitude > 1e-4f ? away.normalized : toFixer, Vector3.up));
            return true;
        }

        // ------------------------------------------------------------ the current note

        // Confirmed (the green check, "I'm done", or a picked idea after a quiet start): the note goes up.
        public void PostTopicNote(Transform note, string text)
        {
            if (!NoteOnWall || note == null || string.IsNullOrWhiteSpace(text)) return;
            RememberWallNote(text);
            var data = Wall.notes.Count > 0 ? Wall.notes[Wall.notes.Count - 1] : null;
            if (data == null) return;
            Vector3 from = note.position;
            Quaternion fromRotation = note.rotation;
            PlaceCurrentNote(data, from, fromRotation);
            if (board != null) arrival.PutNoteOnPad(PadPose.position, PadPose.rotation, BoardScale);
            ToySounds.Play(ToySounds.Cue.Paper, from, .7f, 1.2f);
            Talk.Add("The person's note went up on the " + (Wall.place == "wall" ? "wall" : "board beside Fixer") + ": " + data.text);
            WriteInputLog("note posted (" + (Wall.place ?? "?") + "): " + data.text, true);
        }

        // This make's note on the board (the one there before joins the earlier ideas).
        private void PlaceCurrentNote(WallNote data, Vector3 from, Quaternion fromRotation)
        {
            if (data == null) return;
            if (NoteRewriting) EndRewrite(true);
            if (ringNote != null) SendRingNoteToWall();
            ringData = data; // (set first: PutUpWall does not put this one up with the others)
            if (board == null && !PutUpWall()) { ringData = null; return; }
            var view = WallView(data);
            view.SetPositionAndRotation(from, fromRotation);
            noteOf[view] = data;
            ringNote = view;
            DressCurrent(view);
            StartCoroutine(PostByTeammate(view)); // F82: Fixer carries it up
        }

        // An earlier idea let go on the current note (BringBackIdea): it is the current note now.
        private void MakeCurrent(Transform view)
        {
            if (board == null || view == null) return;
            view.SetParent(board, true);
            looseNotes.Remove(view);
            wallViews.Remove(view);
            if (noteOf.TryGetValue(view, out var data) && data != null && data.pos != null) { data.pos = data.turn = null; SaveWall(); }
            DressCurrent(view);
            StartCoroutine(FlyToCurrent(view));
        }

        private IEnumerator FlyToCurrent(Transform view)
        {
            if (board == null || view == null) yield break;
            Vector3 from = board.InverseTransformPoint(view.position);
            Quaternion fromTurn = Quaternion.Inverse(board.rotation) * view.rotation;
            Vector3 fromScale = view.localScale;
            for (float t = 0f; t < 1f && view != null; t += Time.deltaTime / .65f)
            {
                if (view == carriedNote) yield break;
                float e = Mathf.SmoothStep(0f, 1f, t);
                view.localPosition = Vector3.Lerp(from, Vector3.zero, e) + Vector3.back * Mathf.Sin(t * Mathf.PI) * .1f;
                view.localRotation = Quaternion.Slerp(fromTurn, Quaternion.identity, e);
                view.localScale = Vector3.Lerp(fromScale, Vector3.one * CurrentNoteScale, e);
                yield return null;
            }
            if (view == null || view == carriedNote) yield break;
            view.localPosition = Vector3.zero;
            view.localRotation = Quaternion.identity;
            view.localScale = Vector3.one * CurrentNoteScale;
            ToySounds.Play(ToySounds.Cue.Press, view.position, .5f, 1.6f);
            ToyFx.Puff(view.position, .01f, 4);
            ToyFx.Ring(view.position - view.forward * .01f, view.rotation, .09f);
            if (view == ringNote) WiggleCurrentNote();
        }

        // The Rewrite button in the lower left corner (the words keep to the space above it).
        private void DressCurrent(Transform view)
        {
            if (view == null) return;
            if (rewriteButton != null && rewriteButton.root != null && rewriteButton.root.parent == view) return;
            if (rewriteButton != null && rewriteButton.root != null) Destroy(rewriteButton.root.gameObject);
            var words = view.Find("Words");
            var labels = words != null ? words.GetComponentsInChildren<TextMeshProUGUI>(true) : null;
            currentWords = labels != null && labels.Length > 0 ? labels[0] : null;
            if (currentWords != null)
            {
                var size = currentWords.rectTransform.sizeDelta;
                currentWordsHeight = size.y;
                currentWords.rectTransform.sizeDelta = new Vector2(size.x, Mathf.Min(size.y, CurrentWordsRoom));
            }
            float half = WallSheet * .5f;
            Color blue = MakerRoles.Color(Crew.Maker);
            rewriteButton = MakeRoundButton(view, "Rewrite", new Vector3(-half + .0035f + NoteButtonRadius, -half + .0035f + NoteButtonRadius, -.006f), NoteButtonRadius, Color.white, blue, "noteRewrite");
            rewriteButton.spin = 70f;
            RewriteIcon(rewriteButton, blue);
            rewriteButton.label = RoundLabel(rewriteButton, "Rewrite", Color.Lerp(blue, Color.black, .25f), true);
            NoteRewriting = false;
            if (noteOf.TryGetValue(view, out var data) && data != null) SetCurrentWords(data.text, false);
        }

        // Back among the earlier ideas: the button goes, the words get their whole note again.
        private void StripCurrent(Transform view)
        {
            if (NoteRewriting) { NoteRewriting = false; if (rewriteOld != null) SetCurrentWords(rewriteOld, false); }
            if (rewriteButton != null && rewriteButton.root != null) Destroy(rewriteButton.root.gameObject);
            rewriteButton = null;
            if (currentWords != null && currentWordsHeight > 0f)
            {
                var size = currentWords.rectTransform.sizeDelta;
                currentWords.rectTransform.sizeDelta = new Vector2(size.x, currentWordsHeight);
            }
            currentWords = null;
            currentWordsHeight = -1f;
        }

        private bool CurrentNotePose(out Vector3 at, out Quaternion facing, out Vector3 centre, out float radius)
        {
            at = centre = Vector3.zero;
            facing = Quaternion.identity;
            radius = .05f;
            if (ringNote == null || !ringNote.gameObject.activeInHierarchy) return false;
            at = centre = ringNote.position;
            facing = ringNote.rotation;
            radius = WallSheet * .5f * ringNote.lossyScale.x;
            return true;
        }

        // On or just in front of the current note (where a card, an earlier idea or a picture is let go to use it).
        private bool NearCurrentNote(Vector3 at)
        {
            if (!NoteOnWall || ringNote == null || !ringNote.gameObject.activeInHierarchy || ringNote == carriedNote || float.IsInfinity(at.x)) return false;
            Vector3 d = at - ringNote.position;
            float along = Vector3.Dot(d, ringNote.forward);
            if (along < -.25f || along > .06f) return false;
            float half = WallSheet * .5f * ringNote.lossyScale.x + .05f;
            return Mathf.Abs(Vector3.Dot(d, ringNote.right)) < half && Mathf.Abs(Vector3.Dot(d, ringNote.up)) < half;
        }

        // The i-th shape card on the note: in rows of three under it, on the board, a little crooked.
        private void WallCardSlot(int i, out Vector3 target, out Quaternion turn)
        {
            float s = ringNote.lossyScale.x / CurrentNoteScale;
            int col = i % 3, row = i / 3;
            float x = (col - 1) * .092f, y = -(CurrentHalf + .047f) - row * .078f; // (a words card is wider than a photo)
            target = ringNote.position + ringNote.right * (x * s) + ringNote.up * (y * s) - ringNote.forward * (.006f * s);
            turn = ringNote.rotation * Quaternion.Euler(0f, 0f, i % 2 == 0 ? -2.5f : 2.5f);
        }

        // "Here!" (instead of inking the ring round): the current note gives a little shake.
        public void WiggleCurrentNote()
        {
            if (!NoteOnWall || ringNote == null || ringNote == carriedNote || Time.time < wiggleUntil) return;
            wiggleUntil = Time.time + .55f;
            StartCoroutine(Wiggle(ringNote));
        }

        private IEnumerator Wiggle(Transform view)
        {
            Quaternion rest = view.localRotation;
            for (float t = 0f; t < 1f && view != null && view != carriedNote; t += Time.deltaTime / .5f)
            {
                view.localRotation = rest * Quaternion.Euler(0f, 0f, Mathf.Sin(t * Mathf.PI * 3f) * 5f * (1f - t));
                yield return null;
            }
            if (view != null && view != carriedNote) view.localRotation = rest;
        }

        // ------------------------------------------------------------ rewriting the note

        private void PressRewrite(Hand hand)
        {
            if (ringNote == null || ringData == null || rewriteButton == null) return;
            RoundPressed(rewriteButton);
            Heard();
            if (NoteRewriting)
            {
                EndRewrite(true);
                WriteInputLog("note rewrite: the old words back (pressed again)", true);
                Ack(Crew.Maker, "Okay, I kept your words.");
                return;
            }
            NoteRewriting = true;
            Rewrites++;
            rewriteOld = ringData.text;
            SetCurrentWords(RewriteHint + "\n<size=60%>Hold the mic and talk</size>", true);
            if (rewriteButton.label != null) rewriteButton.label.text = "Keep it";
            Talk.Add("The person cleared their note to say it again.");
            WriteInputLog("note rewrite: cleared, waiting for the words", true);
            Ack(Crew.Maker, "Okay! Hold the mic and say it again.");
        }

        private void EndRewrite(bool restore)
        {
            if (!NoteRewriting) return;
            NoteRewriting = false;
            if (restore && rewriteOld != null) SetCurrentWords(rewriteOld, false);
            if (rewriteButton != null && rewriteButton.label != null) rewriteButton.label.text = "Rewrite";
        }

        private void SetCurrentWords(string text, bool hint)
        {
            if (currentWords == null) return;
            currentWords.text = text;
            currentWords.color = hint ? new Color(.55f, .53f, .5f) : new Color(.2f, .19f, .22f);
            currentWords.fontStyle = hint ? FontStyles.Italic : FontStyles.Normal;
        }

        // WindupsArrival: the words written live on the note while the person talks.
        public void ShowRewriteLive(string said)
        {
            if (!NoteRewriting || string.IsNullOrWhiteSpace(said) || IsRequest(said)) return;
            SetCurrentWords(said.Trim(), false);
        }

        // OnVoice first: while the note waits for new words, what is said is the note (a request to the crew is not).
        private bool RewriteVoice(string text, string p)
        {
            if (!NoteOnWall || !NoteRewriting) return false;
            if (KeepWords(p))
            {
                EndRewrite(true);
                WriteInputLog("note rewrite: the old words back (said)", true);
                Ack(Crew.Maker, "Okay, I kept your words.");
                return true;
            }
            if (IsRequest(text)) return false;
            ApplyRewrite(text);
            return true;
        }

        private static bool KeepWords(string p) =>
            p == "never mind" || p == "nevermind" || p.StartsWith("never mind") || p == "keep it" || p.Contains("keep the old") || p == "cancel" ||
            p.Contains("no change") || p.Contains("그대로") || p.Contains("취소") || p.Contains("원래대로");

        // Before any work the same note gets the new words; after, the old note joins the earlier ideas. Either way the
        // make starts again from the new words.
        private void ApplyRewrite(string said)
        {
            string text = MakerRoles.Clean(said ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 90) text = text.Substring(0, 87) + "...";
            var view = ringNote;
            var old = ringData;
            EndRewrite(true); // (the old words back first: after work began that note joins the earlier ideas as it was)
            if (old != null && string.Equals(old.text, text, StringComparison.OrdinalIgnoreCase))
            {
                SetCurrentWords(old.text, false);
                Ack(Crew.Maker, "Same words! Kept it.");
                return;
            }
            bool started = WorkStarted();
            WriteInputLog("note rewrite: \"" + text + "\"" + (started ? " (a new idea: the old note joins the others)" : " (the same note, new words)"), true);
            Talk.Add("The person rewrote their note: " + text);
            PutAside();
            if (!started && old != null && view != null)
            {
                old.text = text;
                old.at = DateTime.Now.ToString("MMM d");
                Wall.notes.Remove(old);
                Wall.notes.Add(old);
                SaveWall();
                SetCurrentWords(text, false);
                view.name = "Wall Note " + text;
            }
            else
            {
                RememberWallNote(text);
                var data = Wall.notes[Wall.notes.Count - 1];
                PlaceCurrentNote(data, view != null ? view.position : board != null ? board.position : Vector3.zero, view != null ? view.rotation : Quaternion.identity);
            }
            Begin(text);
            Ack(Crew.Maker, started ? "A new idea! Let's start from here." : "Got it!");
        }

        // Has anything been made of the note yet (cards on it, pictures, a make)?
        private bool WorkStarted() =>
            !(Current == Step.Suggesting || Current == Step.ForObject || (Current == Step.ShapeInput && KeptCount == 0 && Looks.Count == 0));

        // ------------------------------------------------------------ notes carried about the wall

        private IEnumerable<Transform> WallCarryables()
        {
            foreach (var v in wallViews) yield return v;
            foreach (var v in looseNotes) yield return v;
            if (NoteOnWall && ringNote != null) yield return ringNote;
        }

        // Let go away from the current note: on the board when close to it, else in the air facing the person.
        private void LeaveNoteWhereLetGo(Transform view, WallNote data)
        {
            if (board == null) { if (view != ringNote) Destroy(view.gameObject); return; }
            bool current = view == ringNote;
            view.SetParent(board, true);
            Vector3 local = board.InverseTransformPoint(view.position);
            Quaternion turn;
            bool onBoard = Mathf.Abs(local.z) * BoardScale < StickToWall;
            if (onBoard && current)
            {
                // F97: the current note is the board's origin: moved along its own wall, the board comes with it.
                if (new Vector2(local.x, local.y).magnitude * BoardScale > .02f)
                    MoveBoardTo(board.TransformPoint(new Vector3(local.x, local.y, 0f)), -board.forward, FindAnchor(Wall.anchor));
                local = Vector3.zero;
            }
            // F97: near another real wall: the current note takes the board there; an earlier note sticks on that wall.
            if (!onBoard && RealWallNear(view.position, out var wallAt, out var wallFacing, out var wallAnchorHit))
            {
                if (current)
                {
                    MoveBoardTo(wallAt, wallFacing, wallAnchorHit);
                    local = Vector3.zero;
                    turn = Quaternion.identity;
                }
                else
                {
                    local = board.InverseTransformPoint(wallAt + wallFacing * .004f);
                    turn = Quaternion.Inverse(board.rotation) * Quaternion.LookRotation(-wallFacing, Vector3.up) * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(-4f, 4f));
                }
                onBoard = true;
            }
            else if (onBoard)
            {
                local.z = 0f;
                turn = current ? Quaternion.identity : Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(-4f, 4f));
            }
            else
            {
                Vector3 away = Head != null ? Vector3.ProjectOnPlane(view.position - Head.position, Vector3.up) : board.forward;
                turn = Quaternion.Inverse(board.rotation) * Quaternion.LookRotation(away.sqrMagnitude > 1e-4f ? away.normalized : board.forward, Vector3.up);
            }
            StartCoroutine(SettleNote(view, local, turn, current ? CurrentNoteScale : 1f));
            if (!current)
            {
                if (!looseNotes.Contains(view)) looseNotes.Add(view);
                if (data != null)
                {
                    data.pos = new[] { local.x, local.y, local.z };
                    data.turn = new[] { turn.x, turn.y, turn.z, turn.w };
                    SaveWall();
                }
            }
            LayOutWall(false);
            WriteInputLog("wall note put down " + (onBoard ? "on a wall" : "in the air") + (current ? " (the current note)" : ""), true);
        }

        private IEnumerator SettleNote(Transform view, Vector3 local, Quaternion turn, float scale)
        {
            Vector3 from = view.localPosition, fromScale = view.localScale;
            Quaternion fromTurn = view.localRotation;
            for (float t = 0f; t < 1f && view != null && view != carriedNote; t += Time.deltaTime / .22f)
            {
                float e = Mathf.SmoothStep(0f, 1f, t);
                view.localPosition = Vector3.Lerp(from, local, e);
                view.localRotation = Quaternion.Slerp(fromTurn, turn, e);
                view.localScale = Vector3.Lerp(fromScale, Vector3.one * scale, e);
                yield return null;
            }
            if (view == null || view == carriedNote) yield break;
            view.localPosition = local;
            view.localRotation = turn;
            view.localScale = Vector3.one * scale;
            ToySounds.Play(ToySounds.Cue.Press, view.position, .4f, 1.8f);
            ToyFx.Puff(view.position, .007f, 3);
        }

        // Notes the person moved before come back where they were put (board-local).
        private void RestoreLooseNotes(List<WallNote> up)
        {
            foreach (var note in up)
            {
                if (note.pos == null || note.pos.Length != 3) continue;
                var view = WallView(note);
                view.localPosition = new Vector3(note.pos[0], note.pos[1], note.pos[2]);
                if (note.turn != null && note.turn.Length == 4) view.localRotation = new Quaternion(note.turn[0], note.turn[1], note.turn[2], note.turn[3]);
                looseNotes.Add(view);
            }
        }

        // ------------------------------------------------------------ pictures on the wall

        // Pinned on the board (above the current note, then leftwards): the first free spot.
        private void PinUpOnBoard(byte[] jpeg, string label, Vector3 from)
        {
            LoadPins();
            string path;
            try
            {
                Directory.CreateDirectory(MoodboardFolder);
                path = Path.Combine(MoodboardFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".jpg");
                File.WriteAllBytes(path, jpeg);
            }
            catch (Exception exception) { Debug.LogWarning("[Windups Moodboard] " + exception.Message); return; }
            // (Where each picture is going, not where it is: pictures pinned together are all still flying.)
            Vector3 slot = board.TransformPoint(BoardPinLocal(0));
            for (int i = 0; i < 12; i++)
            {
                Vector3 p = board.TransformPoint(BoardPinLocal(i));
                bool taken = false;
                foreach (var other in Pins)
                    if (other.world != null && other.world.Length == 3 && Vector3.Distance(new Vector3(other.world[0], other.world[1], other.world[2]), p) < .06f * BoardScale) { taken = true; break; }
                if (!taken) { slot = p; break; }
            }
            Quaternion facing = board.rotation;
            var pin = new Pin { image = path, label = MakerRoles.Clean(label), anchor = Wall.anchor ?? "", at = DateTime.Now.ToString("MMM d"), scale = BoardScale };
            SetPinPlace(pin, slot, facing);
            Pins.Add(pin);
            SavePins();
            var view = PinView(pin);
            view.localScale = Vector3.one * BoardScale;
            view.SetPositionAndRotation(from, facing);
            StartCoroutine(FlyPin(view, slot, facing));
            Talk.Add("Pinned to the idea wall: " + pin.label);
        }

        private static Vector3 BoardPinLocal(int i) => new Vector3(-(i % 4) * .125f, CurrentHalf + .09f + (i / 4) * .14f, -.003f);

        private void SetPinPlace(Pin pin, Vector3 position, Quaternion rotation)
        {
            pin.world = new[] { position.x, position.y, position.z };
            pin.turn = new[] { rotation.x, rotation.y, rotation.z, rotation.w };
            var anchor = FindAnchor(pin.anchor);
            if (anchor != null)
            {
                Vector3 local = anchor.transform.InverseTransformPoint(position);
                Quaternion turn = Quaternion.Inverse(anchor.transform.rotation) * rotation;
                pin.local = new[] { local.x, local.y, local.z };
                pin.rotation = new[] { turn.x, turn.y, turn.z, turn.w };
            }
            else pin.local = pin.rotation = null;
        }

        private void SavePins()
        {
            try { File.WriteAllText(MoodboardPath, JsonConvert.SerializeObject(new MoodboardData { pins = Pins }, Formatting.Indented)); }
            catch (Exception exception) { Debug.LogWarning("[Windups Moodboard] " + exception.Message); }
        }

        // A picture gone: its file, its place on the list and its view.
        private void RemovePin(Pin pin, Transform view)
        {
            int vi = view != null ? PinViews.IndexOf(view) : -1;
            int pi = pin != null ? Pins.IndexOf(pin) : vi;
            if (pi >= 0 && pi < Pins.Count)
            {
                string image = Pins[pi].image;
                Pins.RemoveAt(pi);
                try { if (!string.IsNullOrEmpty(image) && File.Exists(image)) File.Delete(image); } catch { }
            }
            if (vi >= 0) PinViews.RemoveAt(vi);
            if (view != null) { pinX.Remove(view); Destroy(view.gameObject); }
            SavePins();
        }

        // OnPinch: a picture on the wall is picked up (grab & release; HeldBy "wall picture").
        private bool CarryPin(Hand hand, Vector3 point, Ray? ray, Pin pin, Transform view)
        {
            if (pinCarry != null || view == null) return false;
            float far = -1f;
            bool near = !float.IsInfinity(point.x) && Vector3.Distance(point, view.position) < .08f * view.lossyScale.x;
            if (!near && ray.HasValue && new Plane(view.forward, view.position).Raycast(ray.Value, out float d) && d > 0f) far = d;
            pinCarry = new PinCarry { pin = pin, view = view, hand = hand, far = far, homePos = view.position, homeRot = view.rotation };
            pinCarry.offset = far > 0f || float.IsInfinity(point.x) ? Vector3.zero : view.position - point;
            pinPath.Clear();
            ToySounds.Play(ToySounds.Cue.Peel, view.position, .5f, 1.2f);
            Heard();
            Talk.Add("The person took a picture off the wall: " + (pin?.label ?? "?"));
            WriteInputLog("wall picture taken" + (far > 0f ? " from afar" : ""), true);
            return true;
        }

        // OnPinch: a crumpled picture lying about is picked up again.
        private bool PinBallPinch(Hand hand, Vector3 point)
        {
            if (pinCarry != null || looseBalls.Count == 0 || float.IsInfinity(point.x)) return false;
            PaperBall best = null;
            float bestD = .045f;
            foreach (var b in looseBalls)
            {
                if (b == null) continue;
                float d = Vector3.Distance(point, b.transform.position);
                if (d < bestD) { bestD = d; best = b; }
            }
            if (best == null) return false;
            looseBalls.Remove(best);
            best.Hold();
            pinCarry = new PinCarry { ball = best, hand = hand, offset = best.transform.position - point };
            pinPath.Clear();
            ToySounds.Play(ToySounds.Cue.Paper, best.transform.position, .4f, 1.2f);
            return true;
        }

        private void PinCarryFrame()
        {
            var c = pinCarry;
            if (c == null) return;
            if (c.view == null && c.ball == null) { pinCarry = null; return; }
            bool live = c.target.HasValue || (c.hand != null && c.hand.isActiveAndEnabled && HandIO.Connected(c.hand) && (HandIO.Pinching(c.hand) || HandFist(c.hand)));
            if (!live) { DropPin(null); return; }
            Transform t = c.ball != null ? c.ball.transform : c.view;
            Vector3 target;
            Ray? aim = null;
            if (c.target.HasValue) target = c.target.Value;
            else if (c.far > 0f && HandIO.Pointer(c.hand, out Pose pointer)) { aim = new Ray(pointer.position, pointer.forward); target = aim.Value.GetPoint(c.far); }
            else target = PinchPoint(c.hand) + c.offset;
            // Aimed at the current note from afar: the picture comes in front of it; a ball at the basket comes over it.
            if (aim.HasValue && c.ball == null && RingPose(out var at, out var noteFacing, out var centre, out float radius))
            {
                float along = Vector3.Dot(centre - aim.Value.origin, aim.Value.direction);
                if (along > .1f && Vector3.Distance(aim.Value.GetPoint(along), centre) < radius + .1f) target = at - noteFacing * Vector3.forward * .05f;
            }
            if (aim.HasValue && c.ball != null && TrashShown)
            {
                float along = Vector3.Dot(TrashPoint - aim.Value.origin, aim.Value.direction);
                if (along > .1f && Vector3.Distance(aim.Value.GetPoint(along), TrashPoint) < .1f) target = TrashPoint + Vector3.up * .05f;
            }
            float blend = c.target.HasValue ? 1f : 1f - Mathf.Exp(-Time.deltaTime * 20f);
            t.position = Vector3.Lerp(t.position, target, blend);
            if (c.ball == null) t.rotation = Quaternion.Slerp(t.rotation, SpeechBubbleDock.FacingAt(t.position), 1f - Mathf.Exp(-Time.deltaTime * 10f));
            pinPath.Add((t.position, Time.time));
            if (pinPath.Count > 8) pinPath.RemoveAt(0);
            if (c.ball == null)
            {
                bool over = NearCurrentNote(t.position);
                if (over && !c.overNote) ToySounds.Play(ToySounds.Cue.Pop, t.position, .3f, 1.8f);
                c.overNote = over;
                PinCrumpleGesture(c);
            }
            else if (cast.TryGetValue(Crew.Fixer, out var fixer) && fixer != null) fixer.lookTarget = c.ball.transform;
        }

        // The holding hand closes into a fist for a moment, or the other hand pinches the picture too.
        private void PinCrumpleGesture(PinCarry c)
        {
            if (c.ball != null || c.view == null || c.hand == null) return;
            if (HandFist(c.hand))
            {
                if (c.fistSince < 0f) c.fistSince = Time.time;
                if (Time.time - c.fistSince > .3f) { CrumpleCarriedPin(); return; }
            }
            else c.fistSince = -1f;
            foreach (var other in HandSet.Both())
            {
                if (other == null || other == c.hand || !other.isActiveAndEnabled || !HandIO.Connected(other) || !HandIO.Pinching(other)) continue;
                if (Vector3.Distance(PinchPoint(other), c.view.position) < .07f * c.view.lossyScale.x) { CrumpleCarriedPin(); return; }
            }
        }

        private void CrumpleCarriedPin()
        {
            var c = pinCarry;
            if (c == null || c.view == null) return;
            Vector3 at = c.view.position;
            string label = c.pin?.label;
            RemovePin(c.pin, c.view);
            c.view = null;
            c.pin = null;
            c.ball = MakePinBall(at);
            c.fistSince = -1f;
            PinsCrumpled++;
            Talk.Add("The person crumpled a picture from the wall" + (string.IsNullOrEmpty(label) ? "." : ": " + label));
            WriteInputLog("wall picture crumpled", true);
        }

        private PaperBall MakePinBall(Vector3 at)
        {
            var ball = PaperBall.Make(transform, material, UnityEngine.Random.Range(0, 4));
            ball.transform.localScale = Vector3.one * PaperBall.Radius * 2.3f;
            ball.transform.position = at;
            ball.Hold();
            ToySounds.Play(ToySounds.Cue.Paper, at, .7f, .8f);
            ToyFx.Puff(at, .008f, 4);
            return ball;
        }

        private Vector3 PinVelocity()
        {
            if (pinPath.Count < 2) return Vector3.zero;
            var a = pinPath[0];
            var b = pinPath[pinPath.Count - 1];
            return b.time - a.time > .01f ? (b.at - a.at) / (b.time - a.time) : Vector3.zero;
        }

        private void DropPin(Vector3? thrown)
        {
            var c = pinCarry;
            pinCarry = null;
            if (c == null) return;
            Vector3 velocity = thrown ?? PinVelocity();
            if (c.ball != null) { ReleasePinBall(c.ball, velocity); return; }
            var view = c.view;
            if (view == null) return;
            if (c.pin != null && NearCurrentNote(view.position))
            {
                WriteInputLog("wall picture let go on the note: a reference", true);
                UsePin(c.pin, view);
                StartCoroutine(FlyPin(view, c.homePos, c.homeRot));
                return;
            }
            // On the idea wall's board or the real wall when close to it, else in the air facing the person.
            Vector3 position = view.position;
            Quaternion rotation = Quaternion.identity;
            bool stuck = false;
            if (board != null)
            {
                var plane = new Plane(board.forward, board.position);
                if (Mathf.Abs(plane.GetDistanceToPoint(position)) < StickToWall)
                {
                    position = plane.ClosestPointOnPlane(position) - board.forward * .003f;
                    rotation = board.rotation;
                    stuck = true;
                }
            }
            if (!stuck && FindWall())
            {
                var plane = new Plane(wallNormal, wallPoint);
                if (Mathf.Abs(plane.GetDistanceToPoint(position)) < StickToWall)
                {
                    position = plane.ClosestPointOnPlane(position) + wallNormal * .008f;
                    rotation = Quaternion.LookRotation(-wallNormal, Vector3.up);
                    stuck = true;
                }
            }
            if (!stuck)
            {
                Vector3 away = Head != null ? Vector3.ProjectOnPlane(position - Head.position, Vector3.up) : Vector3.forward;
                rotation = Quaternion.LookRotation(away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward, Vector3.up);
            }
            view.SetPositionAndRotation(position, rotation);
            ToySounds.Play(ToySounds.Cue.Press, position, .4f, 1.7f);
            ToyFx.Puff(position, .008f, 3);
            if (c.pin != null) { SetPinPlace(c.pin, position, rotation); c.pin.scale = view.localScale.x; SavePins(); }
            WriteInputLog("wall picture put down " + (stuck ? "on its wall" : "in the air"), true);
        }

        // The ball let go: into the basket (near it, or thrown at it), else it falls where it is.
        private void ReleasePinBall(PaperBall ball, Vector3 velocity)
        {
            if (ball == null) return;
            Vector3 at = ball.transform.position;
            bool into = false;
            if (TrashShown)
            {
                Vector3 to = TrashPoint - at;
                Vector3 flatV = Vector3.ProjectOnPlane(velocity, Vector3.up), flatTo = Vector3.ProjectOnPlane(to, Vector3.up);
                bool near = to.magnitude < TrashReach;
                bool thrown = velocity.magnitude > TrashThrowSpeed && flatV.sqrMagnitude > 1e-4f && Vector3.Angle(flatV, flatTo) < TrashThrowAngle && to.magnitude < 2.5f;
                into = near || thrown;
            }
            if (into) { StartCoroutine(BallIntoBasket(ball, at)); return; }
            ball.Throw(Vector3.ClampMagnitude(velocity, 2.5f), UnityEngine.Random.insideUnitSphere * 6f);
            if (!looseBalls.Contains(ball)) looseBalls.Add(ball);
            trashUntil = Time.time + 12f;
        }

        private IEnumerator BallIntoBasket(PaperBall ball, Vector3 from)
        {
            looseBalls.Remove(ball);
            ballsFlying++;
            var t = ball.transform;
            Vector3 to = TrashPoint;
            float distance = Vector3.Distance(from, to);
            float seconds = Mathf.Clamp(distance / 1.6f, .18f, .7f);
            for (float k = 0f; k < 1f && t != null; k += Time.deltaTime / seconds)
            {
                t.position = Vector3.Lerp(from, to, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * Mathf.Min(.18f, distance * .35f);
                t.Rotate(420f * Time.deltaTime, 300f * Time.deltaTime, 0f);
                yield return null;
            }
            ballsFlying = Mathf.Max(0, ballsFlying - 1);
            if (t != null) Destroy(t.gameObject);
            Trashed++;
            PinsTrashed++;
            trashUntil = Time.time + TrashLinger;
            ToySounds.Play(ToySounds.Cue.Press, to, .45f, 2.1f); // "tok"
            ToyFx.Puff(to, .008f, 4);
            Body(Crew.Fixer)?.Play("React_Nod");
            Talk.Add("A picture went into Fixer's basket.");
        }

        private bool AnyBallMoving()
        {
            for (int i = looseBalls.Count - 1; i >= 0; i--)
            {
                if (looseBalls[i] == null) { looseBalls.RemoveAt(i); continue; }
                if (!looseBalls[i].Resting) return true;
            }
            return false;
        }

        // The x on a picture: crumpled, into Fixer's basket.
        private void TrashPin(Pin pin, Transform view)
        {
            if (view == null) return;
            Vector3 at = view.position;
            RemovePin(pin, view);
            var ball = MakePinBall(at);
            Talk.Add("The person threw away a picture from the wall.");
            WriteInputLog("wall picture x: into the basket", true);
            LookAt(Crew.Fixer, at, 1.5f);
            trashUntil = Time.time + TrashLinger + 1f;
            if (!TrashShown) ShowTrash();
            if (!TrashShown) { ball.Throw(Vector3.zero, UnityEngine.Random.insideUnitSphere * 4f); looseBalls.Add(ball); return; }
            StartCoroutine(BallIntoBasket(ball, at));
        }

        // The x on a shape card on the note: crumpled, into Fixer's basket (ShapeTrash).
        private void TrashKeptCard(ShapeCard card)
        {
            if (card == null || card.crumpled || card.root == null) return;
            CardsCrossed++;
            Crumple(card);
            if (card.ball == null) return;
            WriteInputLog("shape card x: into the basket", true);
            trashUntil = Time.time + TrashLinger + 1f;
            if (!TrashShown) ShowTrash();
            if (!TrashShown) { card.ball.Throw(Vector3.zero, UnityEngine.Random.insideUnitSphere * 4f); return; }
            StartCoroutine(IntoBasket(card, card.ball.transform.position));
        }

        // ------------------------------------------------------------ every frame

        private void WallItemsFrame()
        {
            if (!NoteOnWall) return;
            PinCarryFrame();
            WallButtonsFrame();
            TouchFinishedNotes();
            if (ShapeCards.Count == 0 && (TrashShown || PinTrashWanted)) TrashFrame(); // (with cards, ShapeCardsFrame runs it)
        }

        // Fingertip presses of the Rewrite button, the pictures' x and the shape cards' x, and their looks.
        private void WallButtonsFrame()
        {
            if (rewriteButton != null && rewriteButton.root != null && ringNote != null)
            {
                rewriteButton.near = float.MaxValue;
                bool pressable = ringNote != carriedNote;
                foreach (var hand in HandSet.Both())
                {
                    if (!Fingertip(hand, out Vector3 tip)) { if (hand != null) rewriteButton.press.Forget(hand); continue; }
                    if (RoundTouch(rewriteButton, hand, tip) && pressable) { PressLog("note rewrite", hand); PressRewrite(hand); break; }
                }
                RoundLook(rewriteButton, NoteRewriting);
            }
            for (int i = 0; i < PinViews.Count; i++)
            {
                var v = PinViews[i];
                if (v == null) continue;
                if (!pinX.TryGetValue(v, out var x) || x == null || x.root == null) pinX[v] = x = MakeX(v, new Vector3(.047f, .052f, -.0045f), Vector3.one);
                bool show = pinCarry == null || pinCarry.view != v;
                if (x.root.gameObject.activeSelf != show) x.root.gameObject.SetActive(show);
                if (!show) continue;
                x.near = float.MaxValue;
                foreach (var hand in HandSet.Both())
                {
                    if (!Fingertip(hand, out Vector3 tip)) { if (hand != null) x.press.Forget(hand); continue; }
                    if (RoundTouch(x, hand, tip)) { PressLog("picture x", hand); RoundPressed(x); TrashPin(i < Pins.Count ? Pins[i] : null, v); return; }
                }
                RoundLook(x, false);
            }
            if (ShapeCards.Count == 0) { if (cardX.Count > 0) cardX.Clear(); return; }
            for (int i = 0; i < ShapeCards.Count; i++)
            {
                var card = ShapeCards[i];
                bool show = card.kept && !card.crumpled && card != carriedCard && card.root != null && card.root.gameObject.activeInHierarchy && ringNote != null;
                cardX.TryGetValue(card, out var x);
                if (!show) { if (x != null && x.root != null && x.root.gameObject.activeSelf) x.root.gameObject.SetActive(false); continue; }
                if (x == null || x.root == null)
                    cardX[card] = x = MakeX(card.root, new Vector3(card.width * .5f - .003f, card.height * .5f - .003f, -.004f), Vector3.one / KeptScale);
                if (!x.root.gameObject.activeSelf) { x.root.gameObject.SetActive(true); x.press.Clear(); }
                x.near = float.MaxValue;
                foreach (var hand in HandSet.Both())
                {
                    if (!Fingertip(hand, out Vector3 tip)) { if (hand != null) x.press.Forget(hand); continue; }
                    if (RoundTouch(x, hand, tip)) { PressLog("shape card x", hand); RoundPressed(x); TrashKeptCard(card); return; }
                }
                RoundLook(x, false);
            }
        }

        private static bool Fingertip(Hand hand, out Vector3 tip)
        {
            tip = Vector3.zero;
            if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose pose)) return false;
            tip = pose.position;
            return true;
        }

        // ------------------------------------------------------------ the note's buttons before the make begins

        // Update (also before Begin): while the person reviews the words on the note in their hand.
        private void TopicButtonsFrame()
        {
            bool want = NoteOnWall && arrival != null && arrival.maker && arrival.CurrentPhase == WindupsArrival.Phase.ReviewingTopic &&
                        arrival.TopicNote != null && arrival.TopicNote.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(arrival.TopicDraft);
            if (!want)
            {
                if (TopicButtonsShown) { topicDone.root.gameObject.SetActive(false); topicRewrite.root.gameObject.SetActive(false); }
                return;
            }
            if (topicDone == null || topicDone.root == null || topicRewrite == null || topicRewrite.root == null) BuildTopicButtons();
            if (topicDone == null || topicRewrite == null) return;
            if (!topicDone.root.gameObject.activeSelf)
            {
                topicDone.root.gameObject.SetActive(true);
                topicRewrite.root.gameObject.SetActive(true);
                topicDone.press.Clear();
                topicRewrite.press.Clear();
            }
            PlaceTopicButtons();
            topicDone.near = topicRewrite.near = float.MaxValue;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null) continue;
                bool live = hand.isActiveAndEnabled && HandIO.Connected(hand);
                bool pinching = live && HandIO.Pinching(hand);
                topicPinchWas.TryGetValue(hand, out bool was);
                topicPinchWas[hand] = pinching;
                if (!Fingertip(hand, out Vector3 tip)) { topicDone.press.Forget(hand); topicRewrite.press.Forget(hand); continue; }
                if (RoundTouch(topicDone, hand, tip)) { PressLog("note check", hand); PressTopicButton(topicDone); return; }
                if (RoundTouch(topicRewrite, hand, tip)) { PressLog("note rewrite", hand); PressTopicButton(topicRewrite); return; }
                if (pinching && !was)
                {
                    Ray? ray = HandIO.Pointer(hand, out Pose pointer) ? new Ray(pointer.position, pointer.forward) : (Ray?)null;
                    var hit = TopicButtonHit(PinchPoint(hand), ray, out _);
                    if (hit != null)
                    {
                        PressLog(hit == topicDone ? "note check (pinch)" : "note rewrite (pinch)", hand);
                        wasPinching[hand] = true; // (the check begins the make: this pinch is not a new one for it)
                        PressTopicButton(hit);
                        return;
                    }
                }
            }
#if UNITY_EDITOR
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && Camera.main != null)
            {
                var hit = TopicButtonHit(Vector3.positiveInfinity, Camera.main.ScreenPointToRay(mouse.position.ReadValue()), out _);
                if (hit != null) { PressTopicButton(hit); return; }
            }
#endif
            RoundLook(topicDone, false);
            RoundLook(topicRewrite, false);
        }

        private void BuildTopicButtons()
        {
            if (material == null || font == null) EnsureCrew();
            if (material == null) return;
            var note = arrival.TopicNote.transform;
            Color blue = MakerRoles.Color(Crew.Maker), green = new Color(.3f, .75f, .42f);
            if (topicRewrite != null && topicRewrite.root != null) Destroy(topicRewrite.root.gameObject);
            if (topicDone != null && topicDone.root != null) Destroy(topicDone.root.gameObject);
            topicRewrite = MakeRoundButton(note, "Note Rewrite", Vector3.zero, NoteButtonRadius, Color.white, blue, "noteRewrite");
            topicRewrite.spin = 70f;
            RewriteIcon(topicRewrite, blue);
            topicRewrite.label = RoundLabel(topicRewrite, "Rewrite", Color.Lerp(blue, Color.black, .25f), true);
            topicDone = MakeRoundButton(note, "Note Done", Vector3.zero, NoteButtonRadius, green, Color.Lerp(green, Color.black, .3f), "noteDone");
            topicDone.spin = -12f;
            CheckIcon(topicDone, Color.white);
        }

        // In the note's lower corners, on its face (the note opens and closes: placed every frame).
        private void PlaceTopicButtons()
        {
            Bounds b = TopicSheetBounds();
            // (The note opened for writing is big: the buttons grow with it, up to 1.6 times.)
            float k = Mathf.Clamp(b.size.x * .055f / NoteButtonRadius, 1f, 1.6f);
            topicRewrite.baseScale = topicDone.baseScale = Vector3.one * k;
            // (The drawn sheet is a little inside its bounds: the margin grows with the note too.)
            float m = Mathf.Max(.004f * k, b.size.x * .04f), r = NoteButtonRadius * k, z = Mathf.Min(b.min.z, 0f) - .004f;
            topicRewrite.root.localPosition = new Vector3(b.min.x + m + r, b.min.y + m + r, z);
            topicDone.root.localPosition = new Vector3(b.max.x - m - r, b.min.y + m + r, z);
        }

        // The visible post-it sheet in the note's own space (the card's mesh is a little bigger than the sheet drawn).
        public Bounds TopicSheetBounds()
        {
            var note = arrival.TopicNote.transform;
            var look = note.Find("Post-it Look");
            if (look != null && look.gameObject.activeInHierarchy)
                for (int i = 0; i < look.childCount; i++)
                {
                    var sheet = look.GetChild(i);
                    if (!sheet.gameObject.activeSelf) continue;
                    var mf = sheet.GetComponentInChildren<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) break;
                    var mb = mf.sharedMesh.bounds;
                    var b = new Bounds(note.InverseTransformPoint(mf.transform.TransformPoint(mb.center)), Vector3.zero);
                    for (int k = 0; k < 8; k++)
                    {
                        var corner = new Vector3((k & 1) == 0 ? mb.min.x : mb.max.x, (k & 2) == 0 ? mb.min.y : mb.max.y, (k & 4) == 0 ? mb.min.z : mb.max.z);
                        b.Encapsulate(note.InverseTransformPoint(mf.transform.TransformPoint(corner)));
                    }
                    return b;
                }
            var filter = arrival.TopicNote.GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null ? filter.sharedMesh.bounds : new Bounds(Vector3.zero, new Vector3(.072f, .072f, .002f));
        }

        private void PressTopicButton(RoundButton b)
        {
            RoundPressed(b);
            if (b == topicDone) { WriteInputLog("note check: the note is done", true); arrival.PressTopicDone(); }
            else { WriteInputLog("note rewrite (before the make): cleared", true); arrival.RewriteTopicNote(); }
        }

        private RoundButton TopicButtonHit(Vector3 point, Ray? ray, out float distance)
        {
            distance = 0f;
            if (!TopicButtonsShown) return null;
            RoundButton pick = null;
            float best = float.MaxValue;
            ButtonHit(topicDone, point, ray, ref best, ref pick);
            ButtonHit(topicRewrite, point, ray, ref best, ref pick);
            if (pick != null) distance = best;
            return pick;
        }

        // UiHit (before Begin): the hand's ray on the note's buttons shows the ring and leaves the pinch to them.
        private bool TopicButtonOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            return TopicButtonsShown && TopicButtonHit(Vector3.positiveInfinity, ray, out distance) != null;
        }

        // ------------------------------------------------------------ the wall's buttons by pinch, ray and mouse

        // 0 nothing, 1 Rewrite, 2 a picture's x (index into PinViews), 3 a shape card's x (index into ShapeCards).
        private int FindWallButton(Vector3 point, Ray? ray, out int index, out float distance)
        {
            index = -1;
            distance = 0f;
            if (!NoteOnWall) return 0;
            int kind = 0;
            float best = float.MaxValue;
            RoundButton pick = null;
            if (rewriteButton != null && ringNote != null && ringNote != carriedNote)
            {
                ButtonHit(rewriteButton, point, ray, ref best, ref pick);
                if (pick != null) kind = 1;
            }
            for (int i = 0; i < PinViews.Count; i++)
            {
                var v = PinViews[i];
                if (v == null || !pinX.TryGetValue(v, out var x) || x == null) continue;
                var before = pick;
                ButtonHit(x, point, ray, ref best, ref pick);
                if (pick != before) { kind = 2; index = i; }
            }
            for (int i = 0; i < ShapeCards.Count; i++)
            {
                if (!cardX.TryGetValue(ShapeCards[i], out var x) || x == null) continue;
                var before = pick;
                ButtonHit(x, point, ray, ref best, ref pick);
                if (pick != before) { kind = 3; index = i; }
            }
            if (kind != 0) distance = best;
            return kind;
        }

        // FarButtonOnRay: a Tap target.
        private bool WallButtonOnRay(Ray ray, out float distance) =>
            FindWallButton(Vector3.positiveInfinity, ray, out _, out distance) != 0 ||
            FinishedNoteHit(Vector3.positiveInfinity, ray, out distance) != null;

        // OnPinch (near, or the hand's ray) and the Editor mouse.
        private bool WallButtonPinch(Hand hand, Vector3 point, Ray? ray)
        {
            int kind = FindWallButton(point, ray, out int index, out _);
            switch (kind)
            {
                case 1: PressLog("note rewrite (pinch)", hand); PressRewrite(hand); return true;
                case 2:
                    var view = PinViews[index];
                    if (pinX.TryGetValue(view, out var px)) RoundPressed(px);
                    PressLog("picture x (pinch)", hand);
                    TrashPin(index < Pins.Count ? Pins[index] : null, view);
                    return true;
                case 3:
                    var card = ShapeCards[index];
                    if (cardX.TryGetValue(card, out var cx)) RoundPressed(cx);
                    PressLog("shape card x (pinch)", hand);
                    TrashKeptCard(card);
                    return true;
                default: return ToggleFinishedNote(point, ray);
            }
        }

        // ------------------------------------------------------------ round buttons

        private RoundButton MakeRoundButton(Transform parent, string name, Vector3 local, float radius, Color face, Color rim, string key)
        {
            var b = new RoundButton { radius = radius };
            b.root = new GameObject(name + " Button").transform;
            b.root.SetParent(parent, false);
            b.root.localPosition = local;
            b.root.localRotation = Quaternion.identity;
            Part(b.root, "Rim", StickerMesh(key + "Rim", () => ToyMeshes.Disc(radius + .0016f, .0012f, rim, Color.Lerp(rim, Color.black, .2f), key + "Rim")), Vector3.zero, 0f);
            b.cap = new GameObject("Cap").transform;
            b.cap.SetParent(b.root, false);
            b.cap.localPosition = new Vector3(0f, 0f, -.0026f);
            b.normal = StickerMesh(key + "Face", () => ToyMeshes.Disc(radius, .0026f, face, Color.Lerp(face, Color.black, .15f), key + "Face"));
            b.lit = StickerMesh(key + "FaceLit", () => ToyMeshes.Disc(radius, .0026f, Color.Lerp(face, rim, .35f), Color.Lerp(face, Color.black, .1f), key + "FaceLit"));
            Part(b.cap, "Face", b.normal, Vector3.zero, 0f);
            b.face = b.cap.Find("Face").GetComponent<MeshFilter>();
            b.icon = new GameObject("Icon").transform;
            b.icon.SetParent(b.cap, false);
            b.icon.localPosition = new Vector3(0f, 0f, -.0003f);
            return b;
        }

        // The x of a picture or card: a coral button in its upper right corner.
        private RoundButton MakeX(Transform parent, Vector3 local, Vector3 scale)
        {
            Color coral = new Color(.93f, .38f, .34f);
            var b = MakeRoundButton(parent, "Delete", local, XButtonRadius, coral, Color.white, "wallX");
            b.baseScale = scale;
            b.root.localScale = scale;
            b.spin = 90f;
            var bar = StickerMesh("wallXBar", () => ToyMeshes.RoundedSlab(.0018f, .0085f, .0008f, .0006f, Color.white, Color.white, Color.white, "WallXBar"));
            Part(b.icon, "Bar", bar, Vector3.zero, 45f);
            Part(b.icon, "Bar", bar, Vector3.zero, -45f);
            return b;
        }

        private void RewriteIcon(RoundButton b, Color ink)
        {
            Part(b.icon, "Arrow", StickerMesh("rewriteArrow", () => RewriteArrowMesh(ink)), Vector3.zero, 0f);
            // F79 (Quest: "remove the pink eraser-like thing under the arrow on Rewrite"): the arrow alone.
        }

        private void CheckIcon(RoundButton b, Color ink)
        {
            Part(b.icon, "Check", StickerMesh("noteCheckShort", () => ToyMeshes.RoundedSlab(.0025f, .0056f, .001f, .0006f, ink, ink, ink, "NoteCheckShort")), new Vector3(-.0024f, -.0007f, 0f), 45f);
            Part(b.icon, "Check", StickerMesh("noteCheckLong", () => ToyMeshes.RoundedSlab(.0025f, .0104f, .001f, .0006f, ink, ink, ink, "NoteCheckLong")), new Vector3(.0017f, .0014f, 0f), -35f);
        }

        // A round arrow going back round (as on a "redo" key): an arc with its head at the left.
        private static Mesh RewriteArrowMesh(Color colour)
        {
            const float r = .0037f, w = .0015f;
            const int steps = 18;
            var v = new List<Vector3>();
            var c = new List<Color>();
            var n = new List<Vector3>();
            var t = new List<int>();
            float a0 = -60f, a1 = 200f;
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)steps) * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                v.Add(dir * (r - w * .5f));
                v.Add(dir * (r + w * .5f));
                c.Add(colour); c.Add(colour);
                n.Add(Vector3.back); n.Add(Vector3.back);
                if (i == 0) continue;
                int k = v.Count - 4;
                t.AddRange(new[] { k, k + 1, k + 2, k + 2, k + 1, k + 3, k, k + 2, k + 1, k + 2, k + 3, k + 1 });
            }
            float e = a1 * Mathf.Deg2Rad;
            var outward = new Vector3(Mathf.Cos(e), Mathf.Sin(e), 0f);
            var tangent = new Vector3(-Mathf.Sin(e), Mathf.Cos(e), 0f);
            var p = outward * r;
            int h = v.Count;
            v.Add(p + tangent * .0028f);
            v.Add(p + outward * .0026f - tangent * .0004f);
            v.Add(p - outward * .0026f - tangent * .0004f);
            for (int i = 0; i < 3; i++) { c.Add(colour); n.Add(Vector3.back); }
            t.AddRange(new[] { h, h + 1, h + 2, h, h + 2, h + 1 });
            var mesh = new Mesh { name = "RewriteArrow" };
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetNormals(n);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // A short word beside a button (right of it, or left).
        private TextMeshProUGUI RoundLabel(RoundButton b, string text, Color colour, bool right)
        {
            var canvas = new GameObject("Label", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(b.root, false);
            rect.sizeDelta = new Vector2(34f, 10f);
            rect.pivot = new Vector2(right ? 0f : 1f, .5f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(right ? b.radius + .0022f : -(b.radius + .0022f), 0f, -.0015f);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var label = HatIdeaCard.Label(rect, font, 0f, 0f, 34f, 10f, 6f, 7.5f, colour, FontStyles.Bold,
                right ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.MidlineRight);
            label.text = text;
            return label;
        }

        // A fingertip on the button: within its rim, from PressNear in front to PressThrough behind (F67).
        private static bool OnRoundButton(RoundButton b, Vector3 tip, float margin)
        {
            Vector3 d = tip - b.root.position;
            Vector3 f = b.root.forward;
            float along = Vector3.Dot(d, f);
            float radial = (d - f * along).magnitude;
            return radial <= b.radius * b.root.lossyScale.x + margin && along >= PressNear && along <= PressThrough;
        }

        private bool RoundTouch(RoundButton b, Hand hand, Vector3 tip)
        {
            if (b == null || b.root == null || !b.root.gameObject.activeInHierarchy) return false;
            b.near = Mathf.Min(b.near, Vector3.Distance(tip, b.root.position));
            return b.press.Step(this, hand, OnRoundButton(b, tip, PressMargin));
        }

        // A pinch right at the button, or the hand's ray on it (a little slack further away).
        private static void ButtonHit(RoundButton b, Vector3 point, Ray? ray, ref float best, ref RoundButton pick)
        {
            if (b == null || b.root == null || !b.root.gameObject.activeInHierarchy) return;
            float r = b.radius * b.root.lossyScale.x;
            if (!float.IsInfinity(point.x))
            {
                float d = Vector3.Distance(point, b.root.position);
                if (d < r + .012f) { if (d < best) { best = d; pick = b; } return; } // right at it: the near pinch wins
            }
            if (!ray.HasValue || !new Plane(b.root.forward, b.root.position).Raycast(ray.Value, out float along) || along <= 0f || along > 4f || along >= best) return;
            if (Vector3.Distance(ray.Value.GetPoint(along), b.root.position) > r + .004f + along * .01f) return;
            best = along;
            pick = b;
        }

        private void RoundPressed(RoundButton b)
        {
            if (b == null || b.root == null) return;
            b.pressedAt = Time.time;
            ToySounds.Play(ToySounds.Cue.Press, b.root.position, .5f, 1.6f);
            ToyFx.Puff(b.root.position, .004f, 2);
        }

        // Near a fingertip it grows; pressed it squashes, lights up and its icon turns.
        private static void RoundLook(RoundButton b, bool lit)
        {
            if (b == null || b.root == null) return;
            float since = Time.time - b.pressedAt;
            float hover = Mathf.Clamp01(1f - (b.near - b.radius) / .05f);
            float grow = 1f + .2f * hover;
            if (since < .25f) grow *= .85f + .15f * (since / .25f);
            b.root.localScale = b.baseScale * grow;
            b.face.sharedMesh = lit || since < .3f ? b.lit : b.normal;
            float wave = since < .4f ? Mathf.Sin(since / .4f * Mathf.PI) : 0f;
            b.icon.localRotation = Quaternion.Euler(0f, 0f, b.spin * wave);
        }

        // ------------------------------------------------------------ checks

        public void SimulatePressRewrite() => PressRewrite(null);
        public bool SimulateTopicDone() { if (!TopicButtonsShown) return false; PressTopicButton(topicDone); return true; }
        public bool SimulateTopicRewrite() { if (!TopicButtonsShown) return false; PressTopicButton(topicRewrite); return true; }
        public bool SimulatePeelCurrentNote()
        {
            if (ringNote == null || carriedNote != null) return false;
            CarryNote(ringNote, null, ringNote.position, -1f);
            noteTarget = ringNote.position;
            return true;
        }
        public bool SimulatePeelLooseNote(int index)
        {
            if (index < 0 || index >= looseNotes.Count || carriedNote != null) return false;
            var view = looseNotes[index];
            CarryNote(view, null, view.position, -1f);
            noteTarget = view.position;
            return true;
        }
        public Transform LooseNoteView(int index) => index >= 0 && index < looseNotes.Count ? looseNotes[index] : null;
        public bool SimulatePickPin(int index)
        {
            if (index < 0 || index >= PinViews.Count || index >= Pins.Count || pinCarry != null) return false;
            if (!CarryPin(null, PinViews[index].position, null, Pins[index], PinViews[index])) return false;
            pinCarry.target = PinViews[index].position;
            return true;
        }
        public bool SimulatePickPinBall(int index)
        {
            if (index < 0 || index >= looseBalls.Count || pinCarry != null || looseBalls[index] == null) return false;
            if (!PinBallPinch(null, looseBalls[index].transform.position)) return false;
            pinCarry.target = pinCarry.ball.transform.position;
            return true;
        }
        public void SimulateCarryPin(Vector3 to) { if (pinCarry != null) { pinCarry.target = to; PinCarryFrame(); } }
        public void SimulateCrumplePin() { if (pinCarry != null && pinCarry.view != null) CrumpleCarriedPin(); }
        public void SimulateReleasePin(Vector3? thrown = null) { if (pinCarry != null) DropPin(thrown); }
        public bool SimulatePinX(int index)
        {
            if (index < 0 || index >= PinViews.Count) return false;
            TrashPin(index < Pins.Count ? Pins[index] : null, PinViews[index]);
            return true;
        }
        public bool SimulateCardX(ShapeCard card)
        {
            if (card == null || !card.kept || !cardX.ContainsKey(card)) return false;
            TrashKeptCard(card);
            return true;
        }
        public Transform PinXView(int index) => index >= 0 && index < PinViews.Count && pinX.TryGetValue(PinViews[index], out var x) && x != null ? x.root : null;
        public Transform CardXView(ShapeCard card) => card != null && cardX.TryGetValue(card, out var x) && x != null ? x.root : null;
        public void SimulatePinUp(byte[] jpeg, string label, Vector3 from) => PinUp(jpeg, label, from);
        // Checks: the pinch OnPinch would get at `point` / on `ray` (wall buttons first, as OnPinch does).
        public bool SimulateWallButtonPinch(Vector3 point, Ray? ray) => WallButtonPinch(null, point, ray);
        public bool WallButtonUnder(Ray ray) => WallButtonOnRay(ray, out _);
        public bool TopicButtonUnder(Ray ray) => TopicButtonOnRay(ray, out _);
    }
}
