using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Windups.Handoff
{
    // F76 (2026-11-14, the user: "the characters talk too much and I can't keep up; they ask me for an answer when I may
    // not be looking; only one character with buttons at a time; a sound when buttons come; if I don't look, walk my way
    // or hop - 'look at me!'; a soft mumble per character with every bubble; bubbles go too fast; while comparing, the
    // picture a character talks about grows, and its speech bubble's border glows"):
    //  - Calm talk (CalmTalk; the scripted Editor demo keeps the old pacing unless CalmInScripted, so the older checks
    //    keep their timing): a line stays ReadSeconds (2.5 s + 0.45 s a word) and the next one waits until this one was
    //    read: looked at (head within SeeAngle of the bubble) for its reading time, or the person did something since it
    //    showed, or UnseenLinger passed. A bubble never looked at stays up; read, it lingers ReadLinger and shrinks away
    //    (BubbleFade) instead of blinking out. At most QueueKeep lines wait; older ones that are not questions or comments
    //    on a picture are dropped (they were never shown). A short reaction (Ack) with nothing to read (IsFiller) is a
    //    hop, a face and a mumble without a bubble; a longer one waits its turn first in line instead of covering the
    //    bubble being read.
    //  - One ask at a time: the open question's pills are the only request (MakerSession.Questions.cs, F54); the shape
    //    step's Done pill steps aside while a question is open. Request pills come with the Ask chime.
    //  - "Look at me!": a question the person does not look at (NotLookingAngle of the asker or its bubble) for
    //    LookAtMeAfter: the asker hops three times with a happy face, the chime and a mumble; still not seen after
    //    WalkOverAfter: it walks up to WalkOverMost toward the person's view on its own surface (a free spot of the F51
    //    map, never a teleport) and hops again; it walks back to its seat when the question closes.
    //  - Mumble: MakerSession.Mumble.cs.
    //  - The picture being talked about: a line said about a picture (SayAbout: votes, pros and cons, stickers) makes that
    //    picture grow HighlightGrow while its bubble is up, the speaker looks at it, and the speaker's bubble gets a
    //    glowing border in its colour (the user: no frame on the picture, a glow on the bubble).
    //  - F80 (Quest 2026-11-14: "I can't tell what they're looking at while they talk - make the picture grow more, and
    //    highlight the picture being talked about instead of the bubble"): the picture grows HighlightGrow 1.4 (was
    //    1.15) and comes HighlightForward toward the person, the other pictures of its row shrink to HighlightOthers and
    //    dim, and the glowing frame is round the picture (a line about something that is not a picture, e.g. a
    //    version, still glows on the bubble). Looking at the picture counts as reading its line, and while comparing a
    //    line needs CompareReadShare of its reading time (the person reads it while looking at the picture).
    public sealed partial class MakerSession
    {
        public static bool CalmTalk = true;
        public static bool CalmInScripted;
        // F92 (2026-11-15, the user on the Quest: the post-it going up is good, but the shape explanation after it is
        // too slow): a line nobody looks at no longer holds the queue for 20 s, the reading wait is shorter, and while
        // a teammate shows the ways to do something (tutorialRunning) the lines flow on TutorialBeat instead of
        // waiting to be read. OldCalmPacing() puts the F76 numbers back for the checks written against them.
        public static float ReadBase = 1.1f, ReadPerWord = .26f, UnseenLinger = 2.5f, ReadLinger = .8f;
        public static float LineGap = .3f, TutorialBeat = 2.2f;
        public const float BubbleFade = .35f;

        public static void OldCalmPacing()
        {
            ReadBase = 2.5f; ReadPerWord = .45f; UnseenLinger = 20f; ReadLinger = 1.5f; LineGap = .8f; TutorialBeat = 99f;
        }
        public const float LookAtMeAfter = 2.5f, WalkOverAfter = 8f, WalkOverMost = .6f;
        // (checks set these to 0 = never looking, 180 = always looking, as VerifyHandRules does with TagLookAngle)
        public static float SeeAngle = 35f, NotLookingAngle = 40f;
        public const float HighlightGrow = 1.4f, GlowMargin = .005f;
        public const float HighlightOthers = .82f, HighlightForward = .03f, PictureGlowMargin = .011f, CompareReadShare = .65f;
        private static readonly Color DimPicture = new Color(.55f, .55f, .58f, 1f);
        private static readonly Color GlowRim = new Color(1f, .9f, .5f), GlowEdge = new Color(1f, .78f, .32f);
        public const int QueueKeep = 2;

        public int DroppedLines { get; private set; }
        public int FillerReactions { get; private set; }
        public int LookAtMeHops { get; private set; }
        public int LookAtMeWalks { get; private set; }
        public float CaptionSeenFor => captionSeenFor;
        public float CaptionReadSeconds => captionRead;
        public bool CaptionWasRead => CaptionRead();
        public string CaptionText => captioned != null && captioned.authoredBubble != null && captioned.authoredBubble.gameObject.activeInHierarchy ? captioned.authoredBubble.title.text : null;
        public Transform HighlightedThing => highlightRoot;
        public bool BubbleGlowing => glow != null && glow.gameObject.activeInHierarchy;
        public Transform GlowView => glow;
        public Transform CaptionBubble => captioned != null && captioned.authoredBubble != null ? captioned.authoredBubble.transform : null;
        public int LinesWaiting => lines.Count;
        public bool DoneDeferred => Calm && Question != null && !Question.answered;

        private bool Calm => CalmTalk && (!Scripted || CalmInScripted);
        public static float ReadSeconds(string text) => ReadBase + MakerRoles.Words(text) * ReadPerWord;

        private float captionShownAt = -10f, captionSeenFor, captionRead;

        // ------------------------------------------------------------ reading

        // Caption: a line just went up on a bubble.
        private void CaptionShown(Crew crew, WindupCharacter character, string text)
        {
            CancelFade(character);
            captionShownAt = Time.time;
            captionSeenFor = 0f;
            captionRead = ReadSeconds(text) * (Comparing ? CompareReadShare : 1f); // F80
            if (Calm) captionUntil = Mathf.Max(captionUntil, Time.time + captionRead + ReadLinger);
            MumbleFor(crew, text);
        }

        // The line on show has been read: looked at for its reading time, or the person did something since it showed,
        // or it has waited UnseenLinger.
        private bool CaptionRead()
        {
            if (captioned == null || captioned.authoredBubble == null || !captioned.authoredBubble.gameObject.activeInHierarchy) return true;
            if (captionSeenFor >= captionRead) return true;
            if (lastHeardAt > captionShownAt + .2f) return true;
            return Time.time - captionShownAt > UnseenLinger;
        }

        // F92: the step has moved on, so the line still on show from the step before no longer holds the next one back
        // (the first line of a new step used to wait out the old bubble's reading time).
        private void StepChanged()
        {
            if (captioned != null) captionSeenFor = Mathf.Max(captionSeenFor, captionRead);
        }

        // F92: while a teammate is showing the ways to do something, every line comes on the beat (the person is
        // watching the tools, not reading the card), and a question about what they just did still goes first.
        private bool BriskNext => RightAwayNext || tutorialRunning;

        private bool LookingAt(Vector3 point, float angle) => Head != null && Vector3.Angle(Head.forward, point - Head.position) <= angle;

        // Say: more than QueueKeep lines waiting: the oldest that is not a question or a comment on a picture goes.
        private void TrimQueue()
        {
            if (!Calm || CurrentLesson != null || lines.Count <= QueueKeep) return;
            var keep = new List<(Crew crew, string text)>(lines);
            for (int i = 0; i < keep.Count && keep.Count > QueueKeep; i++)
            {
                if (queuedQuestions.ContainsKey(keep[i].text) || lineFocus.ContainsKey(keep[i].text)) continue;
                DroppedLines++;
                WriteInputLog("line dropped (too many waiting): " + MakerRoles.Name(keep[i].crew) + ": " + keep[i].text);
                keep.RemoveAt(i);
                i--;
            }
            lines.Clear();
            foreach (var line in keep) lines.Enqueue(line);
        }

        // 2026-11-15 (Quest: "I gave my picture and it took ages before Spark asked whole or part, and again before Maker
        // asked done or more"): a question that answers what the person just did comes at once. Lines still waiting
        // were about the moment before ("Give it to me!", "Snapped!") and go; the bubble on show counts as read (the
        // person acted) and the question goes first in line, past the reading wait.
        private string rightAway;
        public int RightAwayAsks { get; private set; }
        private void AskNow(Crew crew, string text, bool toCrew, params Reply[] replies)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (toCrew && Question != null && !Question.answered && Question.toCrew && Question.step == Current) return;
            var keep = new List<(Crew crew, string text)>();
            foreach (var line in lines)
                if (queuedQuestions.ContainsKey(line.text) || lineFocus.ContainsKey(line.text)) keep.Add(line);
                else { DroppedLines++; WriteInputLog("line dropped (the person acted): " + MakerRoles.Name(line.crew) + ": " + line.text); }
            lines.Clear();
            foreach (var line in keep) lines.Enqueue(line);
            queuedQuestions[text] = (toCrew, replies ?? new Reply[0]);
            Talk.Add(MakerRoles.Name(crew) + ": " + text);
            LastSay = text;
            LastSpeaker = crew;
            PushFront(crew, text);
            Heard();
            rightAway = text;
            nextLineAt = Mathf.Min(nextLineAt, Time.time);
            RightAwayAsks++;
        }

        // SpeakNext: the line at the head of the queue skips the reading wait.
        private bool RightAwayNext => rightAway != null && lines.Count > 0 && lines.Peek().text == rightAway;

        // A line said next (before the others waiting).
        private void PushFront(Crew crew, string text)
        {
            var rest = lines.ToArray();
            lines.Clear();
            lines.Enqueue((crew, text));
            foreach (var line in rest) lines.Enqueue(line);
            TrimQueue();
        }

        // ------------------------------------------------------------ short reactions

        private static readonly Regex Doing = new Regex(@"\b(hold|tap|press|put|say|pick|take|draw|measure|show|peel|try|look|wait|give|use|drop|grab|pinch|open|close|move|plant|turn|lay|make|tell|need|can't|cannot|couldn't|no)\b", RegexOptions.IgnoreCase);

        // Nothing to read: a few words of cheer or agreement ("Ooh, a new one!", "Got it!", "Back to this one!").
        public static bool IsFiller(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Contains("?") || Regex.IsMatch(text, @"\d")) return false;
            return MakerRoles.Words(text) <= 4 && !Doing.IsMatch(text);
        }

        // Ack in calm talk: filler is a hop, a face and a mumble (no bubble); anything to read waits first in line.
        private bool AckCalm(Crew crew, string text)
        {
            if (!Calm) return false;
            Talk.Add(MakerRoles.Name(crew) + ": " + text); // F48
            if (!cast.TryGetValue(crew, out var c) || c == null) return true;
            if (IsFiller(text))
            {
                FillerReactions++;
                WindupBody.Express(c, text, 1.6f);
                c.SetCue("encourage");
                StartCoroutine(After(.8f, () => { if (c != null && c.Cue == "encourage") c.SetCue("idle"); }));
                StartCoroutine(Bounce(c, 1, .022f));
                ToySounds.Play(ToySounds.Cue.Pop, c.transform.position + Vector3.up * .15f, .35f, 1.3f);
                MumbleFor(crew, text, true);
                return true;
            }
            PushFront(crew, text);
            return true;
        }

        // ------------------------------------------------------------ bubbles shrink away

        private readonly Dictionary<TransferableSpeechBubble, Coroutine> fades = new Dictionary<TransferableSpeechBubble, Coroutine>();

        // HideCaption: gone at once (old pacing) or shrunk away in BubbleFade.
        private void HideBubble(TransferableSpeechBubble bubble)
        {
            if (bubble == null) return;
            if (!Calm || !bubble.gameObject.activeInHierarchy) { bubble.gameObject.SetActive(false); return; }
            if (fades.ContainsKey(bubble)) return;
            fades[bubble] = StartCoroutine(FadeBubble(bubble));
        }

        private IEnumerator FadeBubble(TransferableSpeechBubble bubble)
        {
            var t = bubble.transform;
            string text = bubble.title != null ? bubble.title.text : null;
            for (float k = 0f; k < 1f; k += Time.deltaTime / BubbleFade)
            {
                if (bubble == null) yield break;
                if (bubble.IsHeld || (bubble.title != null && bubble.title.text != text)) { t.localScale = Vector3.one; fades.Remove(bubble); yield break; }
                t.localScale = Vector3.one * Mathf.Lerp(1f, .2f, k * k);
                yield return null;
            }
            if (bubble != null)
            {
                bubble.gameObject.SetActive(false);
                t.localScale = Vector3.one;
            }
            fades.Remove(bubble);
        }

        // Shown again (or another line on it): a shrink under way stops.
        private void CancelFade(WindupCharacter character)
        {
            var bubble = character != null ? character.authoredBubble : null;
            if (bubble == null || !fades.TryGetValue(bubble, out var fade)) return;
            if (fade != null) StopCoroutine(fade);
            fades.Remove(bubble);
            bubble.transform.localScale = Vector3.one;
        }

        // PutAside stops every coroutine: nothing is left half-way (a bubble half shrunk, a hop in the air, a grown picture).
        private void CalmReset()
        {
            foreach (var pair in fades)
                if (pair.Key != null) { pair.Key.transform.localScale = Vector3.one; pair.Key.gameObject.SetActive(false); }
            fades.Clear();
            foreach (var c in cast.Values) if (c != null) c.hop = 0f;
            EndHighlight();
            foreach (var pair in grownBase) if (pair.Key != null) pair.Key.localScale = pair.Value;
            grownBase.Clear();
            grown.Clear();
            lineFocus.Clear();
            StopMumble();
            if (lookAtMeWalked) WalkBackAfterAsk();
            lookAtMeQuestion = null;
            unseenSince = -1f;
            lookAtMeStage = 0;
        }

        // ------------------------------------------------------------ every frame

        private void CalmFrame()
        {
            if (Head == null) return;
            if (captioned != null && captioned.authoredBubble != null && captioned.authoredBubble.gameObject.activeInHierarchy && captionSeenFor < captionRead &&
                (LookingAt(captioned.authoredBubble.transform.position, SeeAngle) ||
                 (highlightRoot != null && highlightSpeaker == captioned && LookingAt(highlightRoot.position, SeeAngle)))) // F80: or at the picture it is about
            {
                captionSeenFor += Time.deltaTime;
                if (captionSeenFor >= captionRead) captionUntil = Mathf.Max(captionUntil, Time.time + ReadLinger);
            }
            HighlightFrame();
            LookAtMeFrame();
        }

        // ------------------------------------------------------------ "look at me!"

        private OpenQuestion lookAtMeQuestion;
        private float unseenSince = -1f;
        private int lookAtMeStage;
        private Crew lookAtMeCrew;
        private bool lookAtMeWalked;
        private Vector3 lookAtMeHome;

        private void LookAtMeFrame()
        {
            var q = Question;
            bool open = Calm && q != null && !q.answered && q.step == Current;
            if (!open || q != lookAtMeQuestion)
            {
                if (lookAtMeWalked) WalkBackAfterAsk();
                lookAtMeQuestion = open ? q : null;
                unseenSince = -1f;
                lookAtMeStage = 0;
                if (!open) return;
            }
            if (!cast.TryGetValue(q.who, out var c) || c == null || c.movement == null) return;
            var bubble = c.authoredBubble;
            bool seen = LookingAt(c.transform.position + Vector3.up * .08f, NotLookingAngle) ||
                        (bubble != null && bubble.gameObject.activeInHierarchy && LookingAt(bubble.transform.position, NotLookingAngle)) ||
                        (q.root != null && LookingAt(q.root.position, NotLookingAngle));
            if (seen) { unseenSince = -1f; return; }
            if (unseenSince < 0f) { unseenSince = Time.time; return; }
            float waited = Time.time - unseenSince;
            if (lookAtMeStage == 0 && waited >= LookAtMeAfter) { lookAtMeStage = 1; LookAtMe(q.who, c, false); }
            else if (lookAtMeStage == 1 && waited >= WalkOverAfter) { lookAtMeStage = 2; LookAtMe(q.who, c, true); }
        }

        private bool TeammateBusy(Crew crew, WindupCharacter c)
        {
            var pet = c.movement;
            if (pet == null || pet.IsHeld || pet.State == RoomCapsule.CapsulePetController.MotionState.Walking ||
                pet.State == RoomCapsule.CapsulePetController.MotionState.Climbing || pet.State == RoomCapsule.CapsulePetController.MotionState.Falling) return true;
            if (Comparing || InMeeting) return true;
            if (crew == Crew.Maker && (Sculpt != SculptStage.None || Sketching)) return true;
            return crew == Crew.Spark && SparkWalking;
        }

        private void LookAtMe(Crew crew, WindupCharacter c, bool walk)
        {
            if (TeammateBusy(crew, c)) return;
            Vector3 at = c.transform.position + Vector3.up * .12f;
            ToySounds.Play(ToySounds.Cue.Ask, at, .8f, 1f);
            MumbleFor(crew, "hey hey", true, true);
            WriteInputLog("look at me (" + MakerRoles.Name(crew) + "): " + (walk ? "walks over" : "hops"), true);
            if (walk && WalkIntoView(crew, c)) { LookAtMeWalks++; return; }
            LookAtMeHops++;
            StartCoroutine(Bounce(c, 3, .035f));
        }

        // Up to WalkOverMost toward where the person looks, on its own surface, to a free spot.
        private bool WalkIntoView(Crew crew, WindupCharacter c)
        {
            // F82 (Quest: "while I was measuring, Fixer suddenly went far away and couldn't come back"): nobody walks off
            // to be seen while the person measures or has a tool in hand (their eyes are on their hands; a hop is enough).
            if (Current == Step.Measuring || TapeAt != ToolAt.Crew || PaletteAt != ToolAt.Crew || Drawing) return false;
            if (AnyHandWorkedWithin(HandRoomFor)) return false; // F101: not toward hands at work
            var pet = c.movement;
            Vector3 feet = pet.transform.position;
            Vector3 forward = Vector3.ProjectOnPlane(Head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) return false;
            forward.Normalize();
            float distance = Vector3.ProjectOnPlane(feet - Head.position, Vector3.up).magnitude;
            Vector3 want = new Vector3(Head.position.x, feet.y, Head.position.z) + forward * Mathf.Max(.35f, distance * .85f);
            Vector3 step = Vector3.ProjectOnPlane(want - feet, Vector3.up);
            if (step.magnitude > WalkOverMost) step = step.normalized * WalkOverMost;
            // Toward where the person looks; else straight toward the person (that part of its surface is usually clear).
            Vector3 toPerson = Vector3.ProjectOnPlane(Head.position - feet, Vector3.up);
            Vector3 near = feet + toPerson.normalized * Mathf.Min(WalkOverMost * .6f, Mathf.Max(0f, toPerson.magnitude - .3f));
            Vector3 spot = feet;
            bool found = false;
            foreach (var goal in new[] { feet + step, near })
                if (FreeStand(crew, goal, .25f, out spot) && Vector3.ProjectOnPlane(spot - feet, Vector3.up).magnitude >= .06f) { found = true; break; }
            if (!found) return false;
            lookAtMeHome = feet;
            lookAtMeCrew = crew;
            pet.UnlockMeetingSeat();
            if (!pet.GoTo(spot, 1.2f)) { pet.LockMeetingSeat(); return false; }
            lookAtMeWalked = true;
            StartCoroutine(BounceWhenThere(c));
            return true;
        }

        private IEnumerator BounceWhenThere(WindupCharacter c)
        {
            float until = Time.time + 5f;
            yield return new WaitForSeconds(.3f);
            while (c != null && Time.time < until && c.movement.State == RoomCapsule.CapsulePetController.MotionState.Walking) yield return null;
            if (c != null && !c.movement.IsHeld) yield return Bounce(c, 2, .035f);
        }

        private void WalkBackAfterAsk()
        {
            lookAtMeWalked = false;
            if (!cast.TryGetValue(lookAtMeCrew, out var c) || c == null || c.movement == null || c.movement.IsHeld) return;
            c.movement.GoTo(ClearSeat(lookAtMeCrew, lookAtMeHome), 2f);
            StartCoroutine(SeatWhenBack(c.movement));
        }

        private IEnumerator SeatWhenBack(RoomCapsule.CapsulePetController pet)
        {
            float until = Time.time + 6f;
            yield return new WaitForSeconds(.3f);
            while (pet != null && Time.time < until && pet.State == RoomCapsule.CapsulePetController.MotionState.Walking) yield return null;
            for (int i = 0; i < 30 && pet != null && !pet.MeetingSeatLocked; i++) { pet.LockMeetingSeat(); if (!pet.MeetingSeatLocked) yield return null; }
        }

        // Return.cs: a teammate that walked over to ask is away on purpose.
        private bool AskingAway(Crew crew) => lookAtMeWalked && lookAtMeCrew == crew;

        // A few hops in place (the model only; the capsule stays seated): up, squash on landing, a tiny tap.
        private IEnumerator Bounce(WindupCharacter c, int hops, float apex)
        {
            var body = WindupBody.Of(c);
            if (body != null)
            {
                body.Emote(WindupBody.Happy, hops * .36f + .4f);
                if (hops > 1) body.Play("Wave");
            }
            for (int i = 0; i < hops; i++)
            {
                for (float t = 0f; t < 1f; t += Time.deltaTime / .3f)
                {
                    if (c == null || c.movement == null || c.movement.IsHeld) { if (c != null) c.hop = 0f; yield break; }
                    c.hop = apex * 4f * t * (1f - t);
                    yield return null;
                }
                if (c == null) yield break;
                c.hop = 0f;
                body?.Squash(.84f, .16f);
                ToySounds.Play(ToySounds.Cue.Press, c.transform.position, .18f, 2.3f);
                yield return new WaitForSeconds(.05f);
            }
        }

        // ------------------------------------------------------------ the picture being talked about

        private readonly Dictionary<string, Transform> lineFocus = new Dictionary<string, Transform>();
        private readonly Dictionary<Transform, Vector3> grownBase = new Dictionary<Transform, Vector3>();
        private readonly Dictionary<Transform, float> grown = new Dictionary<Transform, float>();
        private readonly List<Transform> grownDone = new List<Transform>(), grownKeys = new List<Transform>();
        private Transform highlightRoot;
        private WindupCharacter highlightSpeaker;
        private float highlightUntil;
        private Transform glow;
        private MeshFilter glowFilter;
        private MeshRenderer glowRenderer;
        private Vector2 glowSize;
        private MaterialPropertyBlock glowBlock;

        // A line about one picture (or idea note, or version): it is highlighted while the line is up.
        private void SayAbout(Crew crew, string text, Transform focus)
        {
            if (focus != null && !string.IsNullOrWhiteSpace(text)) lineFocus[text] = focus;
            Say(crew, text);
        }

        // SpeakNext: the line just went up.
        private void LineFocus(Crew crew, string text, float seconds)
        {
            if (!lineFocus.TryGetValue(text, out var focus)) { if (highlightRoot != null) EndHighlight(); return; }
            lineFocus.Remove(text);
            if (focus == null || !cast.TryGetValue(crew, out var c) || c == null) { EndHighlight(); return; }
            highlightRoot = focus;
            highlightSpeaker = c;
            highlightUntil = Time.time + Mathf.Max(seconds, ReadSeconds(text));
            if (!grownBase.ContainsKey(focus)) grownBase[focus] = focus.localScale;
            // F80: the other pictures of its row shrink and dim; the glowing frame goes round the picture.
            foreach (var other in Siblings(focus))
            {
                if (!grownBase.ContainsKey(other)) grownBase[other] = other.localScale;
                if (!grown.ContainsKey(other)) grown[other] = 1f;
            }
            if (Grows(focus)) GlowOnPicture(focus); else GlowOn(c);
            WriteInputLog("highlight: " + MakerRoles.Name(crew) + " talks about " + focus.name);
        }

        private void EndHighlight()
        {
            highlightRoot = null;
            highlightSpeaker = null;
            GlowOff();
        }

        // The speaker of the highlighted line looks at its picture (LateUpdate's look targets).
        private Transform HighlightLook(WindupCharacter c) => highlightRoot != null && c == highlightSpeaker ? highlightRoot : null;

        // Pictures and idea notes grow; a made thing (a version) only gets the look and the glow (its size is real).
        private bool Grows(Transform t)
        {
            foreach (var p in Looks) if (p != null && p.root == t) return true;
            foreach (var i in Ideas) if (i != null && i.root == t) return true;
            return false;
        }

        // FocusLook: the weighed look keeps its highlight size.
        private float GrowOf(Transform t) => t != null && grown.TryGetValue(t, out float f) ? f : 1f;

        private void HighlightFrame()
        {
            if (highlightRoot != null && (highlightSpeaker == null || captioned != highlightSpeaker || Time.time > highlightUntil)) EndHighlight();
            if (highlightRoot != null && !grown.ContainsKey(highlightRoot)) grown[highlightRoot] = 1f;
            if (grown.Count == 0) { GlowFrame(); return; } // (nothing grown: no work, no lists)
            grownDone.Clear();
            grownKeys.Clear();
            grownKeys.AddRange(grown.Keys);
            foreach (var t in grownKeys)
            {
                if (t == null) { grownDone.Add(t); continue; }
                float want = t == highlightRoot && Grows(t) ? HighlightGrow : highlightRoot != null && SameRow(t, highlightRoot) ? HighlightOthers : 1f;
                float f = Mathf.MoveTowards(grown[t], want, Time.deltaTime * 3f); // F80: 1.2 -> 3 (it shows at once)
                grown[t] = f;
                if (grownBase.TryGetValue(t, out var baseScale) && (Focused == null || Focused.root != t)) t.localScale = baseScale * f;
                if (Mathf.Approximately(f, 1f) && t != highlightRoot && !(highlightRoot != null && SameRow(t, highlightRoot))) grownDone.Add(t);
            }
            DimFrame();
            foreach (var t in grownDone)
            {
                grown.Remove(t);
                if (t != null && grownBase.TryGetValue(t, out var baseScale) && (Focused == null || Focused.root != t)) t.localScale = baseScale;
                grownBase.Remove(t);
            }
            GlowFrame();
        }

        // ------------------------------------------------------------ F80: the picture itself is highlighted

        // The other pictures of the same row (looks or idea notes).
        private readonly List<Transform> siblings = new List<Transform>(8);
        private List<Transform> Siblings(Transform focus)
        {
            siblings.Clear();
            foreach (var p in Looks) if (p != null && p.root != null && p.root != focus && SameRow(p.root, focus)) siblings.Add(p.root);
            foreach (var i in Ideas) if (i != null && i.root != null && i.root != focus && SameRow(i.root, focus)) siblings.Add(i.root);
            return siblings;
        }

        private bool IsLook(Transform t) { foreach (var p in Looks) if (p != null && p.root == t) return true; return false; }
        private bool IsIdea(Transform t) { foreach (var i in Ideas) if (i != null && i.root == t) return true; return false; }
        private bool SameRow(Transform a, Transform b) => a != null && b != null && a != b && ((IsLook(a) && IsLook(b)) || (IsIdea(a) && IsIdea(b)));

        // How far the highlighted picture comes toward the person (MakerSession's picture glide adds it to its home).
        private Vector3 HighlightNudge(Transform t)
        {
            if (t == null || t != highlightRoot || Head == null) return Vector3.zero;
            float f = Mathf.InverseLerp(1f, HighlightGrow, GrowOf(t));
            Vector3 toHead = Head.position - t.position;
            return toHead.sqrMagnitude > 1e-6f ? toHead.normalized * HighlightForward * f : Vector3.zero;
        }

        // The other looks dim while one is talked about.
        private bool dimming;
        public bool PicturesDimmed => dimming;
        private void DimFrame()
        {
            bool want = highlightRoot != null && IsLook(highlightRoot);
            if (!want && !dimming) return;
            bool any = false;
            float step = Time.deltaTime * 5f;
            foreach (var p in Looks)
            {
                if (p == null || p.image == null) continue;
                Color to = want && p.root != highlightRoot ? DimPicture : Color.white;
                Color now = p.image.color;
                Color c = new Color(Mathf.MoveTowards(now.r, to.r, step), Mathf.MoveTowards(now.g, to.g, step), Mathf.MoveTowards(now.b, to.b, step), now.a);
                p.image.color = c;
                any |= c.r < .999f;
            }
            dimming = want || any;
        }

        // Size of a picture or idea note (its face), for the frame round it.
        private Vector2 FaceSize(Transform t)
        {
            foreach (var p in Looks) if (p != null && p.root == t) return new Vector2(p.size, p.size);
            foreach (var i in Ideas) if (i != null && i.root == t) return new Vector2(i.width, i.height);
            return new Vector2(.15f, .15f);
        }

        public bool PictureGlowing => glow != null && glow.gameObject.activeInHierarchy && glowOnPicture;
        private bool glowOnPicture;
        // F82 (Quest: "the highlight outline round the compared pictures doesn't fit, and could be prettier"): sized to the
        // whole card (the picture with its name strip, which sits lower), a hollow rounded frame just outside it - a bright
        // cream inner line fading to warm gold - and three small sparkles twinkling at its corners.
        public const float FrameGap = -.0015f, FrameWidth = .0065f; // (overlapping the card edge a little: no dark seam between them)
        private static readonly Color FrameInner = new Color(1f, .97f, .78f), FrameOuter = new Color(1f, .7f, .26f);
        private readonly List<Transform> sparkles = new List<Transform>(3);
        private Mesh sparkleMesh;
        private void GlowOnPicture(Transform picture)
        {
            if (material == null) return;
            EnsureGlow();
            glow.SetParent(picture, false);
            glowOnPicture = true;
            glowSize = Vector2.zero;
            CardFrame(picture, out Vector2 size, out Vector2 centre);
            float w = size.x + FrameGap * 2f, h = size.y + FrameGap * 2f;
            var mesh = RoundRing(w, h, Mathf.Min(.014f, Mathf.Min(w, h) * .14f), FrameWidth, FrameInner, FrameOuter, "PictureFrame");
            if (glowFilter.sharedMesh != null) { owned.Remove(glowFilter.sharedMesh); Destroy(glowFilter.sharedMesh); }
            owned.Add(mesh);
            glowFilter.sharedMesh = mesh;
            glow.localPosition = new Vector3(centre.x, centre.y, -.001f); // in the card's own plane, round its edge
            glow.localRotation = Quaternion.identity;
            glow.gameObject.SetActive(true);
            // Sparkles at three corners (top-left, top-right, bottom-left: Fixer's sticker sits lower right).
            if (sparkleMesh == null) { sparkleMesh = Sparkle(new Color(1f, .99f, .9f), FrameOuter); owned.Add(sparkleMesh); }
            while (sparkles.Count < 3)
            {
                var go = new GameObject("Sparkle", typeof(MeshFilter), typeof(MeshRenderer));
                go.GetComponent<MeshFilter>().sharedMesh = sparkleMesh;
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                go.transform.SetParent(glow, false);
                sparkles.Add(go.transform);
            }
            float x = w * .5f + FrameWidth * .3f, y = h * .5f + FrameWidth * .3f;
            sparkles[0].localPosition = new Vector3(-x, y, -.002f);
            sparkles[1].localPosition = new Vector3(x, y, -.002f);
            sparkles[2].localPosition = new Vector3(-x, -y, -.002f);
            foreach (var sp in sparkles) if (sp != null) sp.gameObject.SetActive(true);
        }

        // The card a picture or idea note is drawn on: its size and its middle (in the root's own space).
        private void CardFrame(Transform t, out Vector2 size, out Vector2 centre)
        {
            centre = Vector2.zero;
            foreach (var p in Looks)
                if (p != null && p.root == t) { size = new Vector2(p.size + .016f, p.size + .034f); centre = new Vector2(0f, -.008f); return; } // MakePicture's card
            foreach (var i in Ideas) if (i != null && i.root == t) { size = new Vector2(i.width, i.height); return; }
            size = new Vector2(.15f, .15f);
        }

        // A hollow rounded rectangle `width` wide, coloured from `inner` (its inside edge) to `outer`; both faces.
        private static Mesh RoundRing(float w, float h, float r, float width, Color inner, Color outer, string name)
        {
            const int perCorner = 8;
            var verts = new List<Vector3>();
            var cols = new List<Color>();
            var tris = new List<int>();
            Vector2[] centres = { new Vector2(w * .5f - r, h * .5f - r), new Vector2(-w * .5f + r, h * .5f - r), new Vector2(-w * .5f + r, -h * .5f + r), new Vector2(w * .5f - r, -h * .5f + r) };
            int n = 0;
            for (int c = 0; c < 4; c++)
                for (int k = 0; k <= perCorner; k++)
                {
                    float a = (c * 90f + k * 90f / perCorner) * Mathf.Deg2Rad;
                    var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    var pIn = centres[c] + d * r;
                    var pOut = centres[c] + d * (r + width);
                    verts.Add(new Vector3(pIn.x, pIn.y, 0f)); cols.Add(inner);
                    verts.Add(new Vector3(pOut.x, pOut.y, 0f)); cols.Add(outer);
                    n++;
                }
            for (int i = 0; i < n; i++)
            {
                int a0 = i * 2, a1 = a0 + 1, b0 = ((i + 1) % n) * 2, b1 = b0 + 1;
                tris.AddRange(new[] { a0, b0, a1, a1, b0, b1, a0, a1, b0, a1, b1, b0 });
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            var normals = new Vector3[verts.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.back;
            mesh.normals = normals;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // A four-pointed sparkle, 1 cm across (white middle, gold tips).
        private static Mesh Sparkle(Color middle, Color tips)
        {
            var verts = new List<Vector3> { Vector3.zero };
            var cols = new List<Color> { middle };
            for (int i = 0; i < 8; i++)
            {
                float a = i * 45f * Mathf.Deg2Rad, len = i % 2 == 0 ? .005f : .0016f;
                verts.Add(new Vector3(Mathf.Cos(a) * len, Mathf.Sin(a) * len, 0f));
                cols.Add(i % 2 == 0 ? tips : middle);
            }
            var tris = new List<int>();
            for (int i = 0; i < 8; i++) { int a = 1 + i, b = 1 + (i + 1) % 8; tris.AddRange(new[] { 0, a, b, 0, b, a }); }
            var mesh = new Mesh { name = "FrameSparkle" };
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            var normals = new Vector3[verts.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.back;
            mesh.normals = normals;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private void EnsureGlow()
        {
            if (glow != null) return;
            var go = new GameObject("Speaking Glow", typeof(MeshFilter), typeof(MeshRenderer));
            glow = go.transform;
            glowFilter = go.GetComponent<MeshFilter>();
            glowRenderer = go.GetComponent<MeshRenderer>();
            glowRenderer.sharedMaterial = material;
            glowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            glowRenderer.receiveShadows = false;
            glowBlock = new MaterialPropertyBlock();
        }

        // The speaker's bubble gets a glowing border in its colour.
        private void GlowOn(WindupCharacter c)
        {
            var bubble = c.authoredBubble;
            if (bubble == null || material == null) return;
            EnsureGlow();
            glowOnPicture = false;
            foreach (var sp in sparkles) if (sp != null) sp.gameObject.SetActive(false); // F82
            glow.SetParent(bubble.transform, false);
            glowSize = Vector2.zero;
            glow.gameObject.SetActive(true);
            ShapeGlow(bubble);
        }

        private void GlowOff()
        {
            if (glow == null) return;
            glow.gameObject.SetActive(false);
            glow.SetParent(transform, false);
        }

        private void ShapeGlow(TransferableSpeechBubble bubble)
        {
            var filter = bubble.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return;
            var b = filter.sharedMesh.bounds;
            var size = new Vector2(b.size.x, b.size.y);
            if (Mathf.Abs(size.x - glowSize.x) < .003f && Mathf.Abs(size.y - glowSize.y) < .003f) return;
            glowSize = size;
            // A warm light glow for everyone (Fixer's own colour is near black and read as a grey frame).
            float w = size.x + GlowMargin * 2f, h = size.y + GlowMargin * 2f;
            var mesh = ToyMeshes.RoundedSlab(w, h, Mathf.Min(.016f, Mathf.Min(w, h) * .3f), .0008f, GlowRim, GlowEdge, GlowRim, "SpeakingGlow");
            if (glowFilter.sharedMesh != null) { owned.Remove(glowFilter.sharedMesh); Destroy(glowFilter.sharedMesh); }
            owned.Add(mesh);
            glowFilter.sharedMesh = mesh;
            glow.localPosition = new Vector3(b.center.x, b.center.y, b.max.z + .0006f);
            glow.localRotation = Quaternion.identity;
        }

        private void GlowFrame()
        {
            if (glow == null || !glow.gameObject.activeSelf || highlightSpeaker == null) return;
            if (glowOnPicture)
            {
                if (highlightRoot == null || glow.parent != highlightRoot) { GlowOff(); return; }
            }
            else
            {
                var bubble = highlightSpeaker.authoredBubble;
                if (bubble == null || glow.parent != bubble.transform) { GlowOff(); return; }
                ShapeGlow(bubble);
            }
            float pulse = .5f + .5f * Mathf.Sin(Time.time * 6f);
            glowRenderer.GetPropertyBlock(glowBlock);
            glowBlock.SetColor("_EmissionColor", GlowRim * (.3f + .4f * pulse));
            glowRenderer.SetPropertyBlock(glowBlock);
            glow.localScale = glowOnPicture ? Vector3.one * (1f + .01f * pulse) : new Vector3(1f + .015f * pulse, 1f + .015f * pulse, 1f);
            if (glowOnPicture)
                for (int i = 0; i < sparkles.Count; i++)
                {
                    if (sparkles[i] == null) continue;
                    float tw = .5f + .5f * Mathf.Sin(Time.time * 5f + i * 2.1f);
                    sparkles[i].localScale = Vector3.one * Mathf.Lerp(.55f, 1.25f, tw);
                    sparkles[i].localRotation = Quaternion.Euler(0f, 0f, Time.time * 40f + i * 30f);
                }
        }

        // ------------------------------------------------------------ checks

        public void SimulateSayAbout(Crew crew, string text, Transform focus) => SayAbout(crew, text, focus);
        public void SimulateAck(Crew crew, string text) => Ack(crew, text);
        public void SimulateSay(Crew crew, string text) => Say(crew, text);
        public void SimulateAsk(Crew crew, string text, params string[] replies)
        {
            var r = new Reply[replies.Length];
            for (int i = 0; i < replies.Length; i++) r[i] = Rp(replies[i]);
            AskWith(crew, text, false, r);
        }
        public void SimulateHeard() => Heard();
    }
}
