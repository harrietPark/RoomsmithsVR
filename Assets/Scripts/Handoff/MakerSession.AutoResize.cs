using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Windups.Handoff
{
    // F88 (Quest 2026-11-15, the user making a horse: "the size suddenly came out a mess. I'd like a button for the really
    // natural, perfect size - could Fixer ask 'auto-resize?' after every make, v1 or v2?").
    // What happened: the sizes given one by one (the hand tape caught 325 mm for "how wide", the usual 20 mm was taken for
    // "how deep", 90 mm for "how tall") each stretched their own side of the model, and the horse's own length runs front to
    // back, so it was squashed to 2% of its length and stretched 13 times in thickness.
    // Now, after every make in the shape review (v1, v2, ...), when the made model is stretched away from its own
    // proportions (one side scaled more than StretchTolerance times another), Fixer asks "It looks stretched. Auto-resize it
    // to its natural shape?" [Auto-resize] [Keep as is] before Maker's Sculpt / Change shape / Start colouring, which waits
    // behind it (F54) and comes back after the answer. Auto-resize puts the model back in its own proportions at the same
    // overall bulk (the geometric mean of the sides' scales, so the box it fills keeps its volume), shrunk to fit a common
    // printer (PrintBedMm) if it would not, and Maker rebuilds it in place (same version, paint kept). Said words work too:
    // "auto resize", "natural size", "fix the proportions", "비율", "자연스럽게"..., and yes/no while the question is up.
    // Only in the shape review (before colouring): rebuilding later would restart the colouring.
    public sealed partial class MakerSession
    {
        public static bool OfferAutoResize = true;          // false: no question after a make (older checks)
        public static float StretchTolerance = 1.12f;       // the largest side scale over the smallest that still counts as natural
        public const string AutoResizeLabel = "Auto-resize", KeepSizeLabel = "Keep as is";
        public const string StretchedLine = "It looks stretched. Auto-resize it to its natural shape?";

        private string resizeQuestion;
        private readonly HashSet<Transform> resizeAsked = new HashSet<Transform>();
        public int AutoResizes { get; private set; }
        public int ResizeOffers { get; private set; }
        public string ResizeQuestionText => resizeQuestion ?? "";
        public Vector3 LastNaturalMm { get; private set; }

        // The model's natural proportions at the same box volume (plain arithmetic; tests call it directly).
        // bounds: the model's own size (x across, y up, z front to back); madeMm: the made size of each side in mm
        // (y with the cut base); bedMm: the printer it must fit (x, y, z as bounds).
        public static Vector3 NaturalSizeMm(Vector3 bounds, Vector3 madeMm, Vector3 bedMm, out float stretch)
        {
            stretch = 1f;
            if (bounds.x < 1e-5f || bounds.y < 1e-5f || bounds.z < 1e-5f || madeMm.x <= 0f || madeMm.y <= 0f || madeMm.z <= 0f) return madeMm;
            var k = new Vector3(madeMm.x / bounds.x, madeMm.y / bounds.y, madeMm.z / bounds.z); // mm per model unit, each side
            float most = Mathf.Max(k.x, Mathf.Max(k.y, k.z)), least = Mathf.Min(k.x, Mathf.Min(k.y, k.z));
            stretch = most / least;
            float g = Mathf.Pow(k.x * k.y * k.z, 1f / 3f);
            var mm = bounds * g;
            float fit = Mathf.Min(1f, Mathf.Min(bedMm.x / mm.x, Mathf.Min(bedMm.y / mm.y, bedMm.z / mm.z)));
            mm *= fit;
            return new Vector3(Mathf.Round(mm.x), Mathf.Round(mm.y), Mathf.Round(mm.z));
        }

        // The made model's sides now (mm, y with the cut base), as FitShapes.ModelSource sizes it.
        private static bool MadeSides(CoasterDesign d, out Vector3 bounds, out Vector3 madeMm)
        {
            bounds = madeMm = Vector3.zero;
            if (d == null || d.kind != "model") return false;
            var source = FitShapes.Source(d.model);
            if (source == null) return false;
            bounds = source.bounds.size;
            if (bounds.x < 1e-5f || bounds.y < 1e-5f || bounds.z < 1e-5f) return false;
            float uniform = d.cupDiameter / Mathf.Max(bounds.x, bounds.z);
            madeMm = new Vector3(d.sizeW > 0f ? d.sizeW : uniform * bounds.x, d.sizeH > 0f ? d.sizeH : uniform * bounds.y, d.sizeD > 0f ? d.sizeD : uniform * bounds.z);
            return true;
        }

        public float StretchOf(CoasterDesign d)
        {
            if (!MadeSides(d, out var b, out var made)) return 1f;
            NaturalSizeMm(b, made, PrintBedMm, out float stretch);
            return stretch;
        }

        // EnterShapeReview: a stretched make gets Fixer's question first; Maker's choice waits behind it.
        private bool OfferAutoResizeNow()
        {
            if (!OfferAutoResize || Product == null || Design == null || Design.kind != "model" || changingShape || resizeAsked.Contains(Product)) return false;
            if (StretchOf(Design) < StretchTolerance) return false;
            resizeAsked.Add(Product);
            ResizeOffers++;
            resizeQuestion = StretchedLine;
            Park(Crew.Maker, ShapeLine(), ShapeReplies(), true);
            AskWith(Crew.Fixer, StretchedLine, true, Rp(AutoResizeLabel, "auto resize"), Rp(KeepSizeLabel, "keep the size"));
            Body(Crew.Fixer)?.LookAtPoint(Product.position + Vector3.up * .03f, 2.5f);
            Talk.Add("Fixer saw the made thing is stretched out of its own proportions (" + StretchOf(Design).ToString("0.0") + "x) and asks to auto-resize it.");
            WriteInputLog("auto-resize offered | " + SidesText(Design) + " | stretched " + StretchOf(Design).ToString("0.0") + "x", true);
            StartCoroutine(ResizeAskWatch(Product));
            return true;
        }

        // If the question never shows or goes without an answer (hushed, closed), Maker's choice is not left waiting.
        private IEnumerator ResizeAskWatch(Transform made)
        {
            string asked = resizeQuestion;
            for (float until = Time.time + 40f; Time.time < until && queuedQuestions.ContainsKey(asked); ) yield return null;
            // (A pressed pill is "answered" for a moment before its answer runs: wait until the question is really closed.)
            while (Question != null && Question.text == asked) yield return null;
            yield return null;
            yield return null;
            if (resizeQuestion != asked) yield break; // answered (or asked again)
            resizeQuestion = null;
            if (made == Product && Review == ReviewStage.Shape && Current == Step.Done && (Question == null || Question.answered) && waiting != null)
                ShowShapeChoices();
        }

        // MakerSession.Features.AppAnswer: Fixer's question answered by a pill.
        private bool ResizeAnswered(OpenQuestion q, string say)
        {
            if (q == null || resizeQuestion == null || q.text != resizeQuestion) return false;
            resizeQuestion = null;
            Heard();
            Talk.Add("Person (tapped, answering Fixer): " + say);
            if (say == "auto resize")
            {
                waiting = null; // the rebuilt one brings Maker's choice back itself
                if (!AutoResizeNow()) ShowShapeChoices();
            }
            else Say(Crew.Fixer, "Okay, it stays as it is.");
            return true;
        }

        // Back to its own proportions, rebuilt in place (same version, paint kept), then the review again.
        private bool AutoResizeNow()
        {
            if (Product == null || Design == null || Design.kind != "model" || Review != ReviewStage.Shape || ShapeConfirmed || changingShape ||
                Current != Step.Done || Sculpting || Sculpt != SculptStage.None || Modelling) return false;
            if (!MadeSides(Design, out var bounds, out var made)) return false;
            var natural = NaturalSizeMm(bounds, made, PrintBedMm, out float stretch);
            string before = SidesText(Design);
            var d = Design.Copy();
            d.sizeW = natural.x;
            d.sizeD = natural.z;
            d.sizeH = natural.y;
            d.modelHeight = natural.y - d.cut;
            d.cupDiameter = Mathf.Max(natural.x, natural.z);
            Design = d.Clamped();
            LastNaturalMm = natural;
            AutoResizes++;
            resizeQuestion = null;
            CloseQuestion("auto-resize");
            Hush();
            Talk.Add($"Fixer auto-resized it to its own proportions: {natural.x:0} x {natural.z:0} x {natural.y:0} mm (W x D x H), it was {before}.");
            WriteInputLog("auto-resize | " + before + " -> " + SidesText(Design) + " | was stretched " + stretch.ToString("0.0") + "x", true);
            Note("design", Design.name, Design.Summary);
            Ack(Crew.Fixer, "Back to its own shape!");
            nextAsVersion = false;
            StartCoroutine(FitAndBuild());
            return true;
        }

        private static string SidesText(CoasterDesign d)
        {
            if (!MadeSides(d, out _, out var mm)) return "?";
            return $"W {mm.x:0} x D {mm.z:0} x H {mm.y:0} mm";
        }

        // (Whole phrases only: "proportion" or "자연스럽게" alone are ordinary words while changing or colouring.)
        private static readonly string[] ResizeWords =
        {
            "auto resize", "auto-resize", "autoresize", "auto size", "resize it", "natural size", "natural shape", "its own shape",
            "fix the proportions", "fix its proportions", "fix the proportion", "right proportions", "proportions back", "unstretch", "un-stretch",
            "자동 크기", "자동으로 크기", "크기 자동", "사이즈 자동", "자동 조절", "비율 맞춰", "비율 고쳐", "비율대로", "원래 비율", "자연스러운 크기",
            "자연스러운 사이즈", "자연스럽게 크기", "크기 맞춰", "사이즈 맞춰", "오토 리사이즈", "리사이즈"
        };
        private static bool SaysResize(string p) => p.Contains("resize") || p.Contains("리사이즈") || p.Contains("자동 크기") || p.Contains("크기 자동");
        private static readonly Regex ResizeYes = new Regex(@"^(yes|yeah|yep|sure|ok|okay|please|do it|go ahead)\b|^(응|네|예|좋아|해줘|그래)");
        private static readonly Regex ResizeNo = new Regex(@"^(no|nope|keep it|keep as is|leave it|as it is|it's fine|its fine)\b|^(아니|그대로|괜찮아|됐어)");

        // OnVoice / Route: the resize words, and yes/no while Fixer's question is up.
        private bool ResizeVoice(string text, string p)
        {
            if (p == null) return false;
            bool asking = resizeQuestion != null && ((Question != null && !Question.answered && Question.text == resizeQuestion) || queuedQuestions.ContainsKey(resizeQuestion));
            bool words = ResizeWords.Any(p.Contains);
            if (asking && !words)
            {
                if (ResizeYes.IsMatch(p)) words = true;
                else if (ResizeNo.IsMatch(p))
                {
                    string asked = resizeQuestion;
                    resizeQuestion = null;
                    queuedQuestions.Remove(asked);
                    var q = Question;
                    if (q != null && !q.answered && q.text == asked) { q.answered = true; CloseQuestion("answered"); } // Maker's choice comes back (F54)
                    else if (waiting != null && Review == ReviewStage.Shape) ShowShapeChoices();                           // (it had not shown yet)
                    Say(Crew.Fixer, "Okay, it stays as it is.");
                    return true;
                }
            }
            if (!words) return false;
            if (Product == null || Design == null || Design.kind != "model" || Current != Step.Done) return false;
            if (Review != ReviewStage.Shape || ShapeConfirmed || changingShape)
            {
                if (!SaysResize(p)) return false; // (other words go where they always went)
                Say(Crew.Fixer, changingShape ? "Let's finish the change first, then I'll resize it." : "I can only resize it before the colouring.");
                return true;
            }
            if (StretchOf(Design) < 1.02f)
            {
                if (asking) { resizeQuestion = null; CloseQuestion("answered"); }
                Say(Crew.Fixer, "It's already in its own shape.");
                return true;
            }
            waiting = null;
            if (!AutoResizeNow()) return false;
            return true;
        }

        // Tests: the made model stretched to these sides (mm, W x D x H) and built again in place, as the hand sizes did.
        public void SimulateStretchedMake(Vector3 widthDepthHeightMm)
        {
            if (Product == null || Design == null || Design.kind != "model") return;
            var d = Design.Copy();
            d.sizeW = widthDepthHeightMm.x;
            d.sizeD = widthDepthHeightMm.y;
            d.sizeH = widthDepthHeightMm.z + d.cut;
            d.modelHeight = widthDepthHeightMm.z;
            d.cupDiameter = Mathf.Max(d.sizeW, d.sizeD);
            Design = d.Clamped();
            CloseQuestion("test");
            waiting = null;
            nextAsVersion = false;
            StartCoroutine(FitAndBuild());
        }
    }
}
