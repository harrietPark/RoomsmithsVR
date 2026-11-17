using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Windups.Handoff
{
    // F61 (2026-11-13, after a Quest try): three places where the person did not know what was going on.
    //  1. The shape step: nobody saw that Maker's held-out sketchbook was meant to be taken. The book is bigger while
    //     held out (BookOfferScale) and Maker keeps offering it: every OfferPulseEvery it pushes the book toward the
    //     person and looks from them to the book, until the person has taken it once (no outline glow: the user's
    //     choice). Maker says "take my sketchbook".
    //  2. Measuring: the tape came out of nowhere. Fixer first says why (the hands set the real size of the thing
    //     being made), then asks the sizes THIS object needs, one at a time (SizePlan: 1-3 of width / depth / height,
    //     chosen by the picture job with the sketches, MakerRoles `sizes`; a small rule table if it gave none), each
    //     with what it is for; a size the crew may choose has a "You decide" pill. The first time a hand-taken size
    //     settles, Fixer says it can still be changed before the blue bar fills. All the sizes go on the model
    //     (FitShapes.Model stretches each side), not only the first.
    //  3. The pictures' opinions: three spoken sentences, then Maker reading the three stickers back, were long and
    //     said little. Now the stickers are silent (the sentence opens when a sticker is tapped, F47), each teammate
    //     judges from its own angle for the person's use, and Maker says one line: the one it would pick and why.
    public sealed partial class MakerSession
    {
        // ------------------------------------------------------------ 1. the sketchbook, held out

        public const float OfferPulseEvery = 3.4f, OfferPulseTime = .9f, OfferPush = .016f, OfferLift = .007f, OfferTip = 9f;
        public int OfferPulses { get; private set; }
        public bool BookEverTaken { get; private set; }
        private float offerPulseAt = -1f;
        public static float PaperSizeForTests => PaperSize;
        private bool offerPulseOn;

        // 0 -> 1 -> 0 over one offer; Maker looks at the book as it pushes it out, then back at the person.
        private float OfferPulse(WindupCharacter maker)
        {
            if (BookEverTaken || !offeredBookReady) { offerPulseOn = false; return 0f; }
            if (offerPulseAt < 0f) offerPulseAt = Time.time + 1.2f;
            float t = Time.time - offerPulseAt;
            if (t < 0f) return 0f;
            if (t >= OfferPulseTime)
            {
                offerPulseOn = false;
                offerPulseAt = Time.time + OfferPulseEvery;
                return 0f;
            }
            if (!offerPulseOn)
            {
                offerPulseOn = true;
                OfferPulses++;
                if (paper != null) Body(Crew.Maker)?.LookAtPoint(paper.position, OfferPulseTime * .55f);
            }
            return Mathf.Sin(t / OfferPulseTime * Mathf.PI);
        }

        // Now (as Maker says it): the next offer starts at once.
        private void OfferBookNow() => offerPulseAt = Time.time;

        // ------------------------------------------------------------ 2. the sizes this object needs

        public sealed class SizeStep
        {
            public string axis, label, why;
            public float typical, mm = -1f;
            public bool must, skipped;
        }

        public readonly List<SizeStep> SizePlan = new List<SizeStep>();
        public int SizeIndex { get; private set; } = -1;
        public SizeStep CurrentSize => SizeIndex >= 0 && SizeIndex < SizePlan.Count ? SizePlan[SizeIndex] : null;
        public const string TapeTimerTaughtKey = "thinkmade.tapeTimerTaught";
        // Tests: the plan a scripted picture job gives (scripted answers carry none, so old checks keep one size).
        public static List<SizeAsk> ScriptedSizes;

        private SizeAsk[] aiSizes;
        private bool timerHinted;

        // The picture job's sizes (forms or looks), kept for the measuring step.
        private void TakeSizePlan(SizeAsk[] sizes)
        {
            aiSizes = sizes;
            SizePlan.Clear();
            SizeIndex = -1;
        }

        // The planned key axis ("H" or "W"; depth counts as width), when there is a plan.
        private string PlannedKeyAxis => SizePlan.Count == 0 ? null : SizePlan[0].axis == "H" ? "H" : "W";

        private static readonly Dictionary<string, string> AxisQuestion = new Dictionary<string, string>
            { ["W"] = "how wide", ["D"] = "how deep", ["H"] = "how tall" };

        private List<SizeStep> PlanSizes()
        {
            // Scripted answers carry no sizes: checks of other features keep the one key size (ScriptedSizes sets a plan).
            IEnumerable<SizeAsk> from = aiSizes != null && aiSizes.Length > 0 ? aiSizes
                : Scripted ? (IEnumerable<SizeAsk>)ScriptedSizes ?? new SizeAsk[0] : RuleSizes();
            var plan = new List<SizeStep>();
            foreach (var s in from)
            {
                if (s == null) continue;
                string axis = (s.axis ?? "").Trim().ToUpperInvariant();
                if (axis != "W" && axis != "D" && axis != "H") continue;
                if (plan.Any(p => p.axis == axis)) continue;
                string label = MakerRoles.Clean(s.label ?? "").Trim().TrimEnd('?', '.').ToLowerInvariant();
                if (label.Length == 0 || label.Length > 24 || !LatinOnly(label)) label = AxisQuestion[axis];
                string why = MakerRoles.Clean(s.why ?? "").Trim().TrimEnd('.');
                if (why.Length > 60 || !LatinOnly(why)) why = "";
                plan.Add(new SizeStep { axis = axis, label = label, why = why, must = s.must, typical = s.typical >= 10f && s.typical <= 600f ? Mathf.Round(s.typical) : 0f });
                if (plan.Count == 3) break;
            }
            // Scripted checks of other features (and a job that gave nothing): the one key size, as before.
            if (plan.Count == 0) plan.Add(KeySizeOnly());
            // The first size is the one the scale comes from: width or height first, and it must be given.
            int key = plan.FindIndex(p => p.axis != "D");
            if (key > 0) { var k = plan[key]; plan.RemoveAt(key); plan.Insert(0, k); }
            if (plan[0].axis == "D") plan[0].axis = "W";
            plan[0].must = true;
            return plan;
        }

        private SizeStep KeySizeOnly()
        {
            bool tall = TallThings.Any(w => (Need ?? "").ToLowerInvariant().Contains(w));
            float typical = tall ? TallHint() : Target != null && Target.onSurface && Target.width > .02f ? Mathf.Round(Target.width * 1000f + 20f) : 0f;
            return new SizeStep { axis = tall ? "H" : "W", label = tall ? "how tall" : "how wide", why = "", must = true, typical = typical };
        }

        // When the picture job gave no sizes: what most things of this kind need.
        private IEnumerable<SizeAsk> RuleSizes()
        {
            string n = (Need ?? "").ToLowerInvariant();
            bool Has(params string[] words) => words.Any(n.Contains);
            if (Has("coaster", "mat", "pad", "코스터", "받침"))
                return new[] { new SizeAsk { axis = "W", label = "how wide", why = "a bit wider than what stands on it", must = true } };
            if (Has("tray", "organizer", "organiser", "box", "drawer", "트레이", "정리", "상자"))
                return new[] { new SizeAsk { axis = "W", label = "how wide", why = "the space it fills", must = true },
                               new SizeAsk { axis = "D", label = "how deep", why = "front to back on your desk", must = true },
                               new SizeAsk { axis = "H", label = "how tall", why = "how high the sides go", must = false, typical = 40f } };
            if (TallThings.Any(n.Contains))
                return new[] { new SizeAsk { axis = "H", label = "how tall", why = "tall enough for what goes in", must = true, typical = TallHint() },
                               new SizeAsk { axis = "W", label = "how wide", why = "room for what goes in", must = true } };
            if (Has("stand", "holder", "거치대"))
                return new[] { new SizeAsk { axis = "W", label = "how wide", why = "wide enough for what it holds", must = true },
                               new SizeAsk { axis = "H", label = "how tall", why = "the height you look at", must = false } };
            return new[] { new SizeAsk { axis = "W", label = "how wide", why = "", must = true },
                           new SizeAsk { axis = "H", label = "how tall", why = "", must = false } };
        }

        private static bool LatinOnly(string s) => s.All(c => c < 0x250);

        // "pen holder" from "A pen holder for my desk"; "it" when the words cannot be shown (the font is Latin only).
        private string ThingName()
        {
            string s = (Need ?? "").Trim();
            for (int i = 0; i < 3; i++) s = Regex.Replace(s, @"^(please|i want to make|i want|make me|make|can you make|a|an|the|my|some)\s+", "", RegexOptions.IgnoreCase).Trim();
            s = Regex.Split(s, @"\s+(for|to|that|which|with|on|in)\s+|[,.!?]", RegexOptions.IgnoreCase)[0].Trim().ToLowerInvariant();
            return s.Length == 0 || s.Length > 28 || !LatinOnly(s) || s.Split(' ').Length > 4 ? "it" : s;
        }

        private static string CountWord(int n) => n == 2 ? "Two" : n == 3 ? "Three" : n.ToString();

        // The measuring step of a model: why first, then the sizes one by one.
        private void StartSizePlan()
        {
            SizePlan.Clear();
            SizePlan.AddRange(PlanSizes());
            SizeIndex = 0;
            timerHinted = false;
            BeginSizeBox(); // F75: the sizes build a box on the desk (MakerSession.SizeBox.cs)
            string thing = ThingName();
            // 2026-11-15 (second Quest try: "after 'show me with your hands, I'll make your horse exactly...' I waited ages
            // before PINCH WITH BOTH HANDS and the gif"): the why and the first size are one short line, shown at once
            // (the person just picked a sketch), and the pinch lesson with its card follows after SizeIntroHold - it
            // waited for each line to be read before (up to 20 s each when not looked at).
            sizeIntro = thing == "it" ? "Now the real size, with your hands." : $"Now your {thing}'s real size, with your hands.";
            if (!SizeIntroQuick)
                Say(Crew.Fixer, thing == "it"
                    ? "Now the real size. Show me with your hands, and I'll make it exactly that big."
                    : $"Now the real size. Show me with your hands, and I'll make your {thing} exactly that big.");
            Talk.Add("Sizes to give: " + string.Join(", ", SizePlan.Select(s => s.axis + " " + s.label + (s.must ? "" : " (optional)"))));
            AskSize(true);
            // How to pinch and pull comes after these lines have been read: a lesson starting at once dropped them
            // (it clears what is waiting to be said), and the tape seemed to come out of nowhere.
            StartCoroutine(TapeLessonAfterLines());
        }

        public bool SizeLinesShown { get; private set; }
        private IEnumerator TapeLessonAfterLines()
        {
            SizeLinesShown = false;
            for (float until = Time.time + 25f; Time.time < until && Current == Step.Measuring &&
                 (lines.Count > 0 || queuedQuestions.Count > 0 || Voice.IsSpeaking || Voice.Loading || (!SizeIntroQuick && Time.time < nextLineAt)); ) yield return null;
            // (a few seconds to read the size line; the person reaching for the tape meanwhile ends the wait)
            if (SizeIntroQuick)
                for (float until = Time.time + SizeIntroHold; Time.time < until && Current == Step.Measuring && !HasMeasure && Tape.State != HandTape.Mode.TwoHands; ) yield return null;
            SizeLinesShown = true;
            if (Current == Step.Measuring) TapeLessonOrDemo(); // F49: how to pinch and pull, the first time
        }

        private void AskSize(bool first)
        {
            var s = CurrentSize;
            if (s == null) return;
            HasMeasure = false;
            StopSettle();
            Tape.Reset();
            ShowTape(false);
            Hint = s.typical;
            int n = SizePlan.Count;
            string lead = n == 1 ? "" : SizeIndex == 0 ? CountWord(n) + " sizes. First, " : SizeIndex == n - 1 ? "Last, " : "Next, ";
            string question = lead.Length == 0 ? Capital(s.label) + "?" : lead + s.label + "?";
            string line = question + (s.why.Length > 0 ? " " + Capital(s.why) + "." : "");
            // The usual size and "you decide" (F77: Fixer asks the size and its answers stand over Fixer, in the same
            // bubble as the question; they were Spark's pills before, so the answers stood over the wrong teammate).
            var replies = new List<Reply>();
            if (s.typical > 0f) replies.Add(Rp($"Use {s.typical:0} mm", "use yours"));
            if (!s.must) replies.Add(Rp("You decide", "you decide"));
            if (first && SizeIntroQuick)
            {
                // 2026-11-15: the why and the first size in one short line, at once (no "why" sentence, no "or leave it").
                string text = (sizeIntro ?? "") + " " + question + (s.typical > 0f ? $" Most are about {s.typical:0} mm." : "");
                if (replies.Count > 0) AskNow(Crew.Fixer, text.Trim(), false, replies.ToArray());
                else { Say(Crew.Fixer, text.Trim()); Heard(); }
                return;
            }
            if (replies.Count > 0)
                AskWith(Crew.Fixer, line + " " + (s.typical > 0f ? $"Most are about {s.typical:0} mm." + (s.must ? "" : " Or leave it to us.") : "Or leave this one to us."), false, replies.ToArray());
            else Say(Crew.Fixer, line);
            if (!first) Heard();
        }

        public static bool SizeIntroQuick = true;     // 2026-11-15 (false: the F61 lines, each read before the lesson)
        public static float SizeIntroHold = 3.5f;
        private string sizeIntro;

        // Called when a size is confirmed (ConfirmMeasure): true while there is another size to ask.
        private bool NextSize()
        {
            var s = CurrentSize;
            if (s == null || Kind != "model") return false;
            s.mm = Measured;
            BoxTakeSize(s.axis, Measured); // F75: the box grows by this side (the first one puts it down)
            Talk.Add($"Measured {s.label}: {Measured:0.#} mm");
            if (timerHinted) MarkTaught(TapeTimerTaughtKey);
            return Advance();
        }

        private bool Advance()
        {
            SizeIndex++;
            if (SizeIndex < SizePlan.Count)
            {
                StartCoroutine(AskSizeSoon());
                return true;
            }
            // Done: the key size is what the rest of the make is scaled from.
            SizeIndex = -1;
            var key = SizePlan[0];
            if (key.mm > 0f) Measured = key.mm;
            HasMeasure = true;
            return false;
        }

        private IEnumerator AskSizeSoon()
        {
            yield return new WaitForSeconds(.6f); // the "got it" first
            if (Current == Step.Measuring && CurrentSize != null) AskSize(false);
        }

        // "You decide" (pill or words): a size the crew may choose is skipped; a size the person must give takes
        // the usual one, if there is one.
        private bool SizeVoice(string p)
        {
            var s = CurrentSize;
            if (s == null || Kind != "model") return false;
            if (!new[] { "you decide", "you pick", "your call", "skip", "알아서", "마음대로" }.Any(p.Contains)) return false;
            Heard();
            CloseQuestion("size skipped");
            if (!s.must)
            {
                s.skipped = true;
                Talk.Add("Person left " + s.label + " to the crew");
                Ack(Crew.Fixer, "I'll pick that one.");
                if (!Advance()) ConfirmAfterPlan();
                return true;
            }
            if (s.typical > 0f) { SetMeasure(s.typical, true); ConfirmMeasure(); return true; }
            Ack(Crew.Fixer, "I need this one from you. Pinch it, or say a number.");
            return true;
        }

        // After the last size was skipped: on to the make, as ConfirmMeasure would.
        private void ConfirmAfterPlan()
        {
            StopSettle();
            measureConfirmedAt = Time.time;
            Hush();
            if (!HasSpot) { MugSpot = DeskPointInFront(); HasSpot = true; }
            ClearTags();
            if (BoxReadyNow()) return; // F75: the box waits for Done
            AfterMeasure();
        }

        // The first time a hand-taken size waits on its bar: it can still be changed.
        private void SettleHint()
        {
            if (Taught(TapeTimerTaughtKey) || timerHinted) { Ack(Crew.Fixer, $"{Measured:0.#} millimetres. Got it!"); return; }
            timerHinted = true;
            Caption(Crew.Fixer, $"{Measured:0.#} mm. Not right? Pinch again before the blue bar fills.", SettleSeconds + 1.5f);
        }

        private float MeasuredAxis(string axis)
        {
            foreach (var s in SizePlan) if (s.axis == axis && s.mm > 0f) return s.mm;
            return 0f;
        }

        // Every size given goes on the model; a side not given follows the shape's own proportions.
        private void ApplyMeasuredSizes(CoasterDesign design, Mesh source)
        {
            if (source == null || SizePlan.Count(s => s.mm > 0f) < 2) return; // one size: the uniform scale already has it
            var b = source.bounds.size;
            float w = MeasuredAxis("W"), d = MeasuredAxis("D"), h = MeasuredAxis("H");
            // F88b: turned a quarter, its own x runs front to back: the person's depth goes on x, their width on z.
            if (design.sidesTurned) { float t = w; w = d; d = t; }
            if (w > 0f) design.sizeW = w;
            if (d > 0f) design.sizeD = d;
            if (w > 0f && d <= 0f) design.sizeD = w * b.z / Mathf.Max(1e-5f, b.x);
            if (d > 0f && w <= 0f) design.sizeW = d * b.x / Mathf.Max(1e-5f, b.z);
            if (h > 0f) design.sizeH = h + design.cut;
            if (design.sizeW > 0f) design.cupDiameter = Mathf.Max(design.sizeW, design.sizeD);
            design.modelHeight = h > 0f ? h : design.cupDiameter * b.y / Mathf.Max(1e-5f, Mathf.Max(b.x, b.z));
            Talk.Add("Model sizes: " + $"W {PersonWidthMm(design):0} " + (PersonDepthMm(design) > 0f ? $"D {PersonDepthMm(design):0} " : "") + (h > 0f ? $"H {h:0}" : "") + " mm" + (design.sidesTurned ? " (turned so its long side runs across)" : ""));
        }

        // ------------------------------------------------------------ 3. the pictures' opinions

        public string LastRecommendation { get; private set; } = "";
        private Picture recommended;
        private string recommendedLine = "";

        // A sticker on a fresh picture is silent: its teammate turns to it and points; the sentence opens on a tap.
        private void QuietSticker(Crew crew, Picture picture, string say)
        {
            if (picture == null || picture.root == null) return;
            Body(crew)?.LookAtPoint(picture.root.position, 1.6f);
            if (crew == Crew.Maker) { recommended = picture; recommendedLine = say; }
        }

        private void ForgetRecommendation() { recommended = null; recommendedLine = ""; }

        // Maker's one line after the stickers: the one it would pick and why (the picture gives a little hop).
        private string Recommendation()
        {
            if (recommended == null || recommended.root == null || string.IsNullOrWhiteSpace(recommendedLine)) return "";
            string line = recommendedLine.Trim();
            if (!Regex.IsMatch(line, @"^(i'?d|i would) (pick|go with|choose)", RegexOptions.IgnoreCase))
                line = "I'd pick the " + MakerRoles.Clean(recommended.look?.label ?? "marked one").ToLowerInvariant() + ": " + line.TrimEnd('.') + ".";
            var words = line.Split(' ');
            if (words.Length > 20) line = string.Join(" ", words.Take(20)).TrimEnd(',', ';') + ".";
            StartCoroutine(Bump(recommended.root));
            LastRecommendation = line;
            return line;
        }
    }
}
