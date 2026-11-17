using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F44 (2026-11-12): a teammate's question stays up until it is answered, with the answers to tap.
    // The person asked for it after answering a question whose speech card had already gone: "when they ask,
    // put the choices above the bubble so I can press one without speaking".
    //  - AskWith(crew, text, toCrew, replies) says the line like any other; when its card shows, a row of reply
    //    pills (the asker's colour) pops up just above the card, and the card stays (it is not hidden after its
    //    reading time, nor when someone else speaks). If the asker's card later shows another line, a small copy
    //    of the question stands under the replies, so the question is never lost.
    //  - A reply is pressed with a fingertip (poke), a pinch on it or a ray pinch from afar (any step, also while
    //    measuring), or a mouse click in the Editor. It answers exactly as saying it would: step questions go
    //    through OnVoice (the words each step understands), the crew's own questions (the AI's ask, with its
    //    options) go back to the crew as an answer to that question.
    //  - An accepted answer or a step transition closes it; a required question never expires.
    // F52 (2026-11-12, the user: "sometimes a strange panel shows up that isn't a speech bubble - an explanation?
    // even for 'Can I put it here?' - remove it"): there is no separate question panel any more (F44's small copy,
    // and F50's copy that replaced the bubble during decisions). The question always stays in the asker's own speech
    // bubble; if the asker says something else meanwhile, the bubble comes back to the question afterwards. The pills
    // stay above that bubble (F77: always there, never under the pictures or by the stand).
    public sealed partial class MakerSession
    {
        public sealed class Reply
        {
            public string label, say;
            public Reply(string label, string say) { this.label = label; this.say = say; }
        }

        public sealed class OpenQuestion
        {
            public Crew who;
            public string text;
            public bool toCrew;
            public Step step;
            public float born;
            public bool answered;
            public float lostAt = -1f; // when the asker's bubble stopped showing the question (F52)
            public Transform root, header;
            public readonly List<(Transform chip, Reply reply, Vector2 size)> chips = new List<(Transform, Reply, Vector2)>();
            public readonly List<Renderer> cardParts = new List<Renderer>();
        }

        public OpenQuestion Question { get; private set; }
        public int RepliesPressed { get; private set; }
        public string LastReply { get; private set; } = "";
        public readonly List<string> QuestionLog = new List<string>(); // "opened|closed:why|pressed: question / reply"
        public static float QuestionLife = 120f;
        public const float ChipHeight = .024f, ChipGap = .006f, ChipFont = 12f, RowWidth = .26f;

        private readonly Dictionary<string, (bool toCrew, Reply[] replies)> queuedQuestions = new Dictionary<string, (bool, Reply[])>();
        private readonly HashSet<Hand> replyTouchDown = new HashSet<Hand>();
        private bool answeringByReply;
        private readonly FingerPress replyPress = new FingerPress(); // F60

        // F54 (2026-11-13, the user: "Fixer asked something, then Maker said compare / pros and cons and it was
        // gone - I wanted to answer Fixer"): one question shows at a time, and a teammate's question is never pushed
        // aside. Maker's own prompt or decision that comes meanwhile waits here and comes back once the teammate's
        // question is answered (by a pill or by words); a teammate asking while Maker's choice is up puts that choice
        // here, so it is never lost either. Dropped when the step or the review has moved on.
        private sealed class WaitingQuestion
        {
            public Crew who;
            public string text;
            public Reply[] replies;
            public Step step;
            public ReviewStage review;
            public bool decision;
        }
        private WaitingQuestion waiting;
        private bool personActing; // the person's own words are being routed now (OnVoice)
        public bool HasWaitingQuestion => waiting != null;
        public string WaitingQuestionText => waiting?.text ?? "";
        private bool PersonActing => personActing || answeringByReply;
        // An unanswered teammate's question of this step (or one queued to show with its line).
        private bool CrewQuestionOpen => (Question != null && !Question.answered && Question.toCrew && Question.step == Current) ||
                                         queuedQuestions.Values.Any(v => v.toCrew);

        private void Park(Crew who, string text, Reply[] replies, bool decision)
        {
            waiting = new WaitingQuestion { who = who, text = text, replies = replies ?? new Reply[0], step = Current, review = Review, decision = decision };
            QuestionLog.Add("waiting: " + text);
        }

        // The teammate's question closed: what waited behind it comes back (next frame, after the answer's own work).
        private IEnumerator ReopenWaiting()
        {
            yield return null;
            var w = waiting;
            if (w == null) yield break;
            if (Question != null && !Question.answered && Question.toCrew) yield break; // another teammate question: keep waiting
            waiting = null;
            if (w.step != Current || w.review != Review) { QuestionLog.Add("waiting dropped (moved on): " + w.text); yield break; }
            QuestionLog.Add("back: " + w.text);
            Caption(w.who, w.text, 3600f);
            OpenQuestionCard(w.who, w.text, false, w.replies);
            if (w.decision) { decisionRemindAt = Time.time + DecisionReminderSeconds; decisionReminders = 0; }
        }

        private static Reply Rp(string label, string say = null) => new Reply(label, say ?? label.ToLowerInvariant());

        // "What shall we make today?" (and the welcome back): one pill that starts the idea meeting (shared with
        // F45's "Find ideas" button at the first note; "give me ideas" is what the idea meeting already hears).
        private static Reply[] IdeaReplies => new[] { Rp("Find ideas", "give me ideas") };

        // Says `text` like Say; when it shows, the replies come up above the card (none given: a plain line).
        private void AskWith(Crew crew, string text, bool toCrew, params Reply[] replies)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // Only another teammate's open question stops a teammate's question (F54: Maker's prompt or choice used to
            // stop it here without a word; now it steps back and waits behind it).
            if (toCrew && Question != null && !Question.answered && Question.toCrew && Question.step == Current) return;
            queuedQuestions[text] = (toCrew, replies ?? new Reply[0]);
            Say(crew, text);
        }

        // SpeakNext: a line just showed on its card.
        private void LineShown(Crew crew, string text)
        {
            if (!queuedQuestions.TryGetValue(text, out var q)) return;
            queuedQuestions.Remove(text);
            OpenQuestionCard(crew, text, q.toCrew, q.replies);
        }

        // Is this character's card the open question (kept up)?
        private bool Pinned(WindupCharacter character) =>
            Question != null && !Question.answered && character != null && cast.TryGetValue(Question.who, out var asker) && asker == character && ShowsQuestion(character);

        private bool ShowsQuestion(WindupCharacter character)
        {
            var bubble = character != null ? character.authoredBubble : null;
            return bubble != null && bubble.gameObject.activeInHierarchy && bubble.title != null && bubble.title.text == Question?.text;
        }

        // ------------------------------------------------------------ the reply pills

        private void OpenQuestionCard(Crew crew, string text, bool toCrew, Reply[] replies)
        {
            var open = Question != null && !Question.answered && Question.step == Current ? Question : null;
            if (open != null && open.toCrew)
            {
                if (toCrew) return;
                // F54: Maker's prompt waits behind the teammate's question (unless the person just asked for it).
                if (!PersonActing) { Park(crew, text, replies, false); return; }
                waiting = null;
            }
            else if (open != null && toCrew)
                Park(open.who, open.text, open.chips.Select(c => c.reply).ToArray(), Review != ReviewStage.None); // F54: the choice comes back after the answer
            else if (!toCrew) waiting = null;
            CloseQuestion("newer question");
            var q = new OpenQuestion { who = crew, text = text, toCrew = toCrew, step = Current, born = Time.time };
            q.root = new GameObject("Question Replies").transform;
            q.root.SetParent(transform, false);
            foreach (var reply in replies.Where(r => r != null && !string.IsNullOrWhiteSpace(r.label)).Take(4))
                AddChip(q, reply);
            if (cast.TryGetValue(crew, out var asker) && asker != null && asker.authoredBubble != null)
                q.cardParts.AddRange(asker.authoredBubble.GetComponentsInChildren<Renderer>(true));
            Question = q;
            QuestionLog.Add("opened: " + text + " / " + string.Join(" | ", q.chips.Select(c => c.reply.label)));
            questionSpotAt = -10f; // F77: a new asker, a new look at what can be seen
            PlaceQuestion(1f);
            for (int i = 0; i < q.chips.Count; i++) StartCoroutine(PopIn(q.chips[i].chip, i * .07f));
            ToySounds.Play(ToySounds.Cue.Ask, q.root.position, .7f, 1f); // F76: a request came up
        }

        // One reply pill (the asker's colour), added at the end of the question's row; starts at scale 0 (PopIn).
        private Transform AddChip(OpenQuestion q, Reply reply)
        {
            Color role = MakerRoles.Color(q.who), face = Color.Lerp(role, Color.white, .8f);
            string label = MakerRoles.Clean(reply.label);
            float width = Mathf.Clamp(label.Length * .0068f + .02f, .042f, .12f);
            var chip = Panel("Reply " + label, width, ChipHeight, face, out var chipText, ChipFont);
            chipText.text = label;
            chipText.fontStyle = FontStyles.Bold;
            // F77: a raised button on a socket of the asker's colour (it reads as a button of that teammate and sinks,
            // lights and clicks when pressed).
            MakeRaised(chip, width, ChipHeight, face, role);
            chip.SetParent(q.root, false);
            chip.localScale = Vector3.zero;
            q.chips.Add((chip, reply, new Vector2(width, ChipHeight)));
            return chip;
        }

        // F54: the open question keeps its line and bubble; only its pills change (pills already there stay put,
        // new ones pop in, gone ones shrink away). Used when Draw v2 becomes possible or impossible.
        private void ReplaceReplies(Reply[] replies)
        {
            var q = Question;
            if (q == null || q.answered || q.root == null) return;
            var old = q.chips.ToList();
            q.chips.Clear();
            foreach (var reply in replies.Where(r => r != null && !string.IsNullOrWhiteSpace(r.label)).Take(4))
            {
                int at = old.FindIndex(c => c.reply.label == reply.label);
                if (at >= 0) { q.chips.Add(old[at]); old.RemoveAt(at); }
                else StartCoroutine(PopIn(AddChip(q, reply), .05f));
            }
            foreach (var (chip, _, _) in old) if (chip != null) StartCoroutine(FadeOut(chip));
            replyTouchDown.Clear();
            replyPress.Clear();
            PlaceQuestion(1f);
            QuestionLog.Add("replies now: " + q.text + " / " + string.Join(" | ", q.chips.Select(c => c.reply.label)));
        }

        private static IEnumerator PopIn(Transform t, float delay)
        {
            yield return new WaitForSeconds(delay);
            for (float k = 0f; k < 1f && t != null; k += Time.deltaTime / .22f)
            {
                float s = 1f + Mathf.Sin(k * Mathf.PI) * .18f * (1f - k);
                t.localScale = Vector3.one * Mathf.SmoothStep(0f, 1f, k) * s;
                yield return null;
            }
            if (t != null) t.localScale = Vector3.one;
        }

        // Just above the asker's card while it shows the question; else where its card stands, with the
        // question's small copy under the replies. Rows of at most RowWidth, facing the person like the cards.
        // F77 (2026-11-14, the user: "the buttons are all in different places"): the answers are always just above the
        // asker's own speech bubble (or where it stands while it shows another line) - never under the pictures being
        // compared, by the stand, on the wall note or over another teammate.
        private Vector3 AboveBubble(WindupCharacter asker, List<Renderer> cardParts, bool showing)
        {
            var bubble = asker.authoredBubble;
            if (showing && bubble != null)
            {
                var b = new Bounds(bubble.transform.position, Vector3.zero);
                if (cardParts != null)
                    foreach (var r in cardParts) if (r != null && r.enabled && r.gameObject.activeInHierarchy && !(r is ParticleSystemRenderer)) b.Encapsulate(r.bounds);
                return new Vector3(bubble.transform.position.x, b.max.y + .008f, bubble.transform.position.z);
            }
            Vector3 home = bubble != null && bubble.startingDock != null && bubble.startingDock.anchor != null ? bubble.startingDock.anchor.position : asker.transform.position + Vector3.up * .2f;
            return home + Vector3.up * .01f;
        }

        private void PlaceQuestion(float blend)
        {
            var q = Question;
            if (q == null || q.root == null || !cast.TryGetValue(q.who, out var asker) || asker == null) return;
            Vector3 bottom = AboveBubble(asker, q.cardParts, ShowsQuestion(asker));
            // Rows from the bottom up.
            var rows = new List<List<int>>();
            float used = 0f;
            for (int i = 0; i < q.chips.Count; i++)
            {
                float w = q.chips[i].size.x;
                if (rows.Count == 0 || used + w > RowWidth) { rows.Add(new List<int>()); used = 0f; }
                rows[rows.Count - 1].Add(i);
                used += w + ChipGap;
            }
            float y = 0f, widest = 0f;
            for (int r = 0; r < rows.Count; r++)
            {
                float width = rows[r].Sum(i => q.chips[i].size.x) + ChipGap * (rows[r].Count - 1);
                widest = Mathf.Max(widest, width);
                float x = -width * .5f;
                foreach (int i in rows[r])
                {
                    float w = q.chips[i].size.x;
                    q.chips[i].chip.localPosition = new Vector3(x + w * .5f, y + ChipHeight * .5f, -.002f);
                    x += w + ChipGap;
                }
                y += ChipHeight + ChipGap;
            }
            bottom += InFrontOfLooks(q, bottom);
            var turn = SpeechBubbleDock.FacingAt(bottom);
            Vector3 centre = bottom + turn * Vector3.up * (y * .5f);
            // F77: checked at the pills' own width (it was the widest possible row, 26 cm, which a wall beside a short row "hid").
            Vector3 visible = VisibleNearAsker(centre, widest * .5f + .008f, y * .5f + .008f);
            if (visible != centre) bottom = visible - SpeechBubbleDock.FacingAt(visible) * Vector3.up * (y * .5f);
            bottom += AheadOfBubbles(bottom + SpeechBubbleDock.FacingAt(bottom) * Vector3.up * (y * .5f), widest * .5f + .006f, y * .5f + .006f); // F81: never behind a speech card
            q.root.position = blend >= 1f ? bottom : Vector3.Lerp(q.root.position, bottom, blend);
            q.root.rotation = SpeechBubbleDock.FacingAt(q.root.position);
        }

        // F77: the spot over the asker's bubble is hidden by the room (a cabinet between it and the eyes): the pills stay
        // by the asker - a little higher, nearer the person, or beside - and only when none of those can be seen do they
        // go in front of the person (they used to jump straight there, which put them somewhere new every time).
        // Re-checked every QuestionSpotEvery; meanwhile the last answer is kept (it costs a few ray casts).
        public const float QuestionSpotEvery = .25f;
        private float questionSpotAt = -10f;
        private Vector3 questionSpotOffset;
        public int QuestionSpotFallbacks { get; private set; }

        private Vector3 VisibleNearAsker(Vector3 centre, float halfWidth, float halfHeight)
        {
            if (Head == null) return centre;
            if (Time.time < questionSpotAt) return centre + questionSpotOffset;
            questionSpotAt = Time.time + QuestionSpotEvery;
            questionSpotOffset = Vector3.zero;
            if (PanelVisible(centre, halfWidth, halfHeight, SpeechBubbleDock.FacingAt(centre))) return centre;
            Vector3 toHead = Vector3.ProjectOnPlane(Head.position - centre, Vector3.up);
            toHead = toHead.sqrMagnitude > 1e-4f ? toHead.normalized : Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, toHead);
            Vector3[] near =
            {
                centre + Vector3.up * .07f, centre + Vector3.up * .14f, centre + toHead * .12f + Vector3.up * .04f,
                centre + Vector3.up * .22f, centre + toHead * .22f + Vector3.up * .08f, centre + side * .14f + Vector3.up * .06f,
                centre - side * .14f + Vector3.up * .06f
            };
            foreach (var p in near)
                if (PanelVisible(p, halfWidth, halfHeight, SpeechBubbleDock.FacingAt(p))) { questionSpotOffset = p - centre; return p; }
            QuestionSpotFallbacks++;
            Vector3 front = VisiblePanelSpot(centre, halfWidth, halfHeight);
            questionSpotOffset = front - centre;
            return front;
        }

        // A look picture between the person's eyes and a pill (pictures lowered for a vote, say) would hide it:
        // the pills come that much nearer the person (measured where they would stand, so it does not flicker).
        private Vector3 InFrontOfLooks(OpenQuestion q, Vector3 bottom)
        {
            if (Head == null || Looks.Count == 0) return Vector3.zero;
            Quaternion facing = SpeechBubbleDock.FacingAt(bottom);
            float pull = 0f;
            foreach (var look in Looks)
            {
                if (look.root == null || !look.root.gameObject.activeInHierarchy) continue;
                Bounds b = default;
                bool any = false;
                foreach (var r in look.root.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || r is ParticleSystemRenderer) continue;
                    if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; }
                }
                if (!any) continue;
                foreach (var (chip, _, _) in q.chips)
                {
                    if (chip == null) continue;
                    Vector3 at = bottom + facing * chip.localPosition;
                    Vector3 toPill = at - Head.position;
                    if (b.IntersectRay(new Ray(Head.position, toPill), out float hit) && hit < toPill.magnitude)
                        pull = Mathf.Max(pull, toPill.magnitude - hit + .03f);
                }
            }
            return pull <= 0f ? Vector3.zero : (Head.position - bottom).normalized * Mathf.Min(pull, .3f);
        }

        // Every frame (LateUpdate): placement, closing and the fingertip press.
        private void QuestionFrame()
        {
            var q = Question;
            if (q == null) return;
            if (q.root == null) { Question = null; return; }
            if (!q.answered)
            {
                if (q.step != Current) { CloseQuestion("step moved on"); return; }
                // F50: a required decision has no expiry. Optional tape questions own their separate timeout.
                if (!cast.TryGetValue(q.who, out var asker) || asker == null || !asker.isActiveAndEnabled) { CloseQuestion("asker gone"); return; }
                // F52: no copy of the question; the asker's bubble goes back to it once its other line has been read.
                if (ShowsQuestion(asker) || (asker.authoredBubble != null && asker.authoredBubble.IsHeld)) q.lostAt = -1f;
                else if (q.lostAt < 0f) q.lostAt = Time.time;
                // F77: nor over the asker's own lesson line (Fixer's size answers stay over its bubble while it teaches the tape).
                else if (Time.time - q.lostAt > 4.5f && lines.Count == 0 && !WindupBody.UserTalking && (!Calm || CaptionRead()) &&
                         !(CurrentLesson != null && CurrentLesson.who == q.who)) // F76: not over a bubble still being read
                {
                    q.lostAt = -1f;
                    Caption(q.who, q.text, 3600f);
                }
            }
            PlaceQuestion(1f - Mathf.Exp(-Time.deltaTime * 10f));
            if (q.answered) return;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip))
                {
                    replyPress.Forget(hand);
                    continue;
                }
                int touched = ReplyAt(tip.position, PressMargin, PressNear, PressThrough); // F67: through the pill counts
                // F60: a pointing, free hand resting on it (a hand holding the stand brushed Cancel beside it).
                if (replyPress.Step(this, hand, touched >= 0)) { PressLog("reply " + q.chips[touched].reply.label, hand); PressReply(touched); return; }
            }
        }

        // The reply whose pill is at `point` (a margin around it, `back`..`front` through it), or -1.
        private int ReplyAt(Vector3 point, float margin, float back, float front)
        {
            var q = Question;
            if (q == null || float.IsInfinity(point.x)) return -1;
            int best = -1;
            float bestOff = float.MaxValue;
            for (int i = 0; i < q.chips.Count; i++)
            {
                var (chip, _, size) = q.chips[i];
                if (chip == null || chip.localScale.x < .5f) continue;
                Vector3 l = q.root.InverseTransformPoint(point) - chip.localPosition;
                if (l.z < back || l.z > front) continue;
                // How far outside the pill (0 inside); the nearest pill wins where margins overlap the gap.
                float off = new Vector2(Mathf.Max(0f, Mathf.Abs(l.x) - size.x * .5f), Mathf.Max(0f, Mathf.Abs(l.y) - size.y * .5f)).magnitude;
                if (off <= margin && off < bestOff) { best = i; bestOff = off; }
            }
            return best;
        }

        private int ReplyOnRay(Ray ray)
        {
            var q = Question;
            if (q == null || q.root == null) return -1;
            var plane = new Plane(q.root.forward, q.root.position);
            if (!plane.Raycast(ray, out float d) || d <= 0f || d > 4f) return -1;
            // A ray from afar shakes more: about 1 cm of slack per metre (4-12 mm).
            return ReplyAt(ray.GetPoint(d), Mathf.Clamp(d * .01f, .004f, .012f), -.01f, .01f);
        }

        // A pinch on a reply (near, or aimed from afar) or an Editor click on one.
        private bool QuestionPinch(Vector3 point, Ray? ray)
        {
            if (Question == null || Question.answered) return false;
            int i = ReplyAt(point, .012f, -.03f, .03f);
            if (i < 0 && ray.HasValue) i = ReplyOnRay(ray.Value);
            if (i < 0) return false;
            PressReply(i);
            return true;
        }

        public void PressReply(int index)
        {
            var q = Question;
            if (q == null || q.answered || index < 0 || index >= q.chips.Count) return;
            q.answered = true;
            var (chip, reply, _) = q.chips[index];
            RepliesPressed++;
            LastReply = reply.label;
            QuestionLog.Add("pressed: " + q.text + " / " + reply.label);
            ToySounds.Play(ToySounds.Cue.Press, chip.position, .45f, 1.7f);
            ToyFx.Puff(chip.position, .004f, 2);
            PressRaised(chip); // F77: down, lit, a ripple
            StartCoroutine(AnswerWith(q, index));
        }

        // The pressed pill goes down and lights (F77), the others fade; then the answer goes where saying it would go.
        private IEnumerator AnswerWith(OpenQuestion q, int index)
        {
            for (int i = 0; i < q.chips.Count; i++)
                if (i != index && q.chips[i].chip != null) StartCoroutine(ScaleTo(q.chips[i].chip, Vector3.zero, .18f));
            yield return new WaitForSeconds(.24f);
            var reply = q.chips[index].reply;
            CloseQuestion("answered");
            answeringByReply = true;
            try
            {
                if (q.toCrew && AppAnswer(q, reply.say)) { } // F59: Fixer's cut offer is answered by the app, not the crew AI
                else if (q.toCrew)
                {
                    string asker = MakerRoles.Name(q.who);
                    Talk.Add("Person (tapped, answering " + asker + "): " + reply.say);
                    Heard();
                    // (F48) With the Director on, it reads the answer (it is told the question it answers).
                    if (UseDirector) DirectorHeard(reply.say, q);
                    else StartCoroutine(CrewTurn("said", "(answering " + asker + "'s question \"" + q.text + "\") " + reply.say));
                }
                else if (UseDirector)
                {
                    // (F48) A step's own tap answer is a known command: done at once by the keyword router, no reading.
                    if (reply.say.Length > 0)
                    {
                        Talk.Add("Person (tapped): " + reply.say);
                        Heard();
                        string before = SpeechState();
                        voiceRoute = null;
                        bool taken = Route(reply.say, reply.say.ToLowerInvariant().Trim().TrimEnd('.', '!', '?'));
                        LogSpeech(reply.say, before, "tap: " + (voiceRoute ?? (taken ? "step" : "not taken")));
                    }
                }
                else OnVoice(reply.say);
            }
            finally { answeringByReply = false; }
        }

        // OnVoice: the person said something themselves (not a tapped reply): the open question is answered.
        private void VoiceAnswersQuestion()
        {
            if (answeringByReply || Question == null || Question.answered) return;
            // Speaking alone is not an answer. Buttons, accepted step input, or a Director answer close it.
        }

        private void CloseQuestion(string why)
        {
            var q = Question;
            if (q == null) return;
            Question = null;
            replyTouchDown.Clear();
            QuestionLog.Add("closed: " + why + ": " + q.text);
            if (QuestionLog.Count > 100) QuestionLog.RemoveRange(0, QuestionLog.Count - 100);
            // The asker's card goes with it, unless it is showing something else now.
            if (cast.TryGetValue(q.who, out var asker) && asker != null && asker != captioned && asker.authoredBubble != null && !asker.authoredBubble.IsHeld &&
                asker.authoredBubble.title != null && asker.authoredBubble.title.text == q.text)
                asker.authoredBubble.gameObject.SetActive(false);
            if (q.root != null) StartCoroutine(FadeOut(q.root));
            if (q.toCrew && waiting != null && isActiveAndEnabled) StartCoroutine(ReopenWaiting()); // F54
        }

        private IEnumerator FadeOut(Transform root)
        {
            // F82 (Quest: "after finishing, a 'Finish' shaped like a button stays up and can't be touched"): the ending
            // stops every coroutine (StartEnding) right after the pressed pill began fading, so the half-faded pills
            // stayed. The engine removes them anyway a moment later, whatever happens to this coroutine.
            if (root != null) Destroy(root.gameObject, .25f);
            yield return ScaleTo(root, Vector3.zero, .15f);
            if (root != null) Destroy(root.gameObject);
        }

        // Tests: the open question's text and its reply labels.
        public string QuestionText => Question?.text ?? "";
        public string[] ReplyLabels => Question?.chips.Select(c => c.reply.label).ToArray() ?? new string[0];
        public Vector3 ReplyPosition(int index) => Question != null && index >= 0 && index < Question.chips.Count && Question.chips[index].chip != null ? Question.chips[index].chip.position : Vector3.zero;
        public bool QuestionCardShowing => Question != null && cast.TryGetValue(Question.who, out var c) && ShowsQuestion(c);
        public bool QuestionCopyShowing => Question != null && Question.header != null && Question.header.gameObject.activeInHierarchy;

        // The top of an open question's pills (and its copy) when its asker stands within `radius` of `near`,
        // else -infinity: the look pictures over Maker's bubble go above it so they never cover the pills.
        // The top of a question's pill rows (world height).
        private static float PillsTop(OpenQuestion q)
        {
            float rowsTop = 0f;
            foreach (var (chip, _, size) in q.chips) if (chip != null) rowsTop = Mathf.Max(rowsTop, chip.localPosition.y + size.y * .5f);
            return q.root.TransformPoint(new Vector3(0f, rowsTop, 0f)).y;
        }

        private float QuestionTopNear(Vector3 near, float radius)
        {
            var q = Question;
            if (q == null || q.answered || q.root == null || !cast.TryGetValue(q.who, out var asker) || asker == null) return float.NegativeInfinity;
            Vector3 d = asker.transform.position - near;
            if (new Vector2(d.x, d.z).magnitude > radius) return float.NegativeInfinity;
            float rowsTop = 0f;
            foreach (var (chip, _, size) in q.chips) if (chip != null) rowsTop = Mathf.Max(rowsTop, chip.localPosition.y + size.y * .5f);
            return q.root.TransformPoint(new Vector3(0f, rowsTop, 0f)).y;
        }
    }
}
