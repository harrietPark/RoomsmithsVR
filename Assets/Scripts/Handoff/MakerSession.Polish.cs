using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    // F86 (2026-11-15, the user after a Quest try: "I gave my drawing to Maker and asked it to polish it, because my
    // drawing is too rough. When I hand Maker a drawing it should ask whether to polish it or not, and polish it when I
    // say so - I drew a circle, it can only come out wobbly, and the shape came out strange"). In the shape step a
    // drawing card handed to Maker (F79) is asked about first: "Want me to polish your lines?" [Polish it] [Keep mine]
    // (the F84 question on the open page is not asked then: one question, at the hand-over). Polish = F84's Perfect on
    // every line the card was drawn from (a wobbly circle becomes a circle), worked out in a Task; the card's picture
    // morphs into the clean lines in the hand-over fan, and the card's picture is what the shape sketches copy. Words
    // that ask for it ("polish it", "make it a clean circle", "more evenly round", "다듬어 줘") also polish the newest
    // drawing card later in the shape step and while the shape sketches are shown; then the sketches are drawn again
    // from the clean drawing. Keep mine / no keeps the lines as drawn.
    public sealed partial class MakerSession
    {
        public static bool PolishOnGive = true;
        public const string PolishLine = "Want me to polish your lines?", PolishLabel = "Polish it", KeepMineLabel = "Keep mine";
        public const float PolishMorphSeconds = .8f;
        private const int PolishMorphFrames = 6, PolishSize = 256;

        public int Polishes { get; private set; }
        public int PolishAsks { get; private set; }
        public bool AwaitingPolish => polishCard != null;
        private ShapeCard polishCard;
        private bool polishing;

        // KeepCard: a drawing given to Maker. True when the question was asked (the rest follows its answer).
        private bool AskPolishOnGive(ShapeCard card)
        {
            if (!PolishOnGive || card == null || card.kind != CardKind.Drawing || card.strokes == null || card.strokes.Count == 0 ||
                card.polishAsked || card.polished || ShapeCardsAuto) return false;
            card.polishAsked = true;
            polishCard = card;
            PolishAsks++;
            Body(Crew.Maker)?.Play("HeadTilt");
            LookAt(Crew.Maker, card.Position, 1.4f);
            AskNow(Crew.Maker, PolishLine, false, Rp(PolishLabel, "polish it"), Rp(KeepMineLabel, "keep mine")); // (at once: Calm.cs AskNow)
            WriteInputLog("polish asked (card " + card.id + ", " + card.strokes.Count + " lines)", true);
            return true;
        }

        // OnVoice / Route, before the card's role: the polish question, or a later request to polish the drawing.
        private bool PolishVoice(string text, string p)
        {
            if (polishing) return false;
            if (polishCard != null && Current != Step.ShapeInput) polishCard = null; // the step moved on: the question went with it
            if (polishCard != null)
            {
                var card = polishCard;
                if (!ShapeCards.Contains(card) || card.crumpled) { polishCard = null; return false; }
                if (p == "keep mine" || p.Contains("keep") || IsNo(p) || p.Contains("그대로") || p.Contains("괜찮"))
                {
                    polishCard = null;
                    ClosePolishQuestion("keep mine");
                    Talk.Add("The person kept their drawing as drawn.");
                    WriteInputLog("polish: kept as drawn", true);
                    Ack(Crew.Maker, "Your lines it is!");
                    AfterPolish(card);
                    return true;
                }
                if (p == "polish it" || PolishWords(p) || IsYes(p))
                {
                    polishCard = null;
                    ClosePolishQuestion("polish it");
                    StartCoroutine(PolishCard(card, false));
                    return true;
                }
                return false;
            }
            if (!PolishWords(p)) return false;
            bool sketches = Current == Step.Looks && FormFirst && lookJob == "forms";
            if (Current != Step.ShapeInput && !sketches) return false;
            var drawing = NewestDrawingCard();
            if (drawing == null) return false;
            if (drawing.polished && !sketches) { Ack(Crew.Maker, "It's already polished!"); return true; }
            // Words beyond the polishing itself ("more evenly round") still go to the sketches as shape words.
            if (sketches) ShapeWords = (ShapeWords + ", " + text).Trim(' ', ',');
            StartCoroutine(PolishCard(drawing, sketches));
            return true;
        }

        private static readonly Regex PolishRegex = new Regex(
            @"\b(polish\w*|clean ?up|clean (it|that|this|the lines?)|neat(er|en|ly)?|tidy|make it perfect|perfect (circle|shape|lines?)|evenly|even (circle|round|shape|lines?)|straighten|wobbly|messy|too rough|proper (circle|shape))\b",
            RegexOptions.Compiled);
        public static bool PolishWords(string p) =>
            !string.IsNullOrEmpty(p) && (PolishRegex.IsMatch(p) || p.Contains("다듬") || p.Contains("반듯") || p.Contains("깔끔") || p.Contains("매끈") ||
                                         p.Contains("정리해") || p.Contains("동그랗게") || p.Contains("고르게") || p.Contains("삐뚤"));

        private ShapeCard NewestDrawingCard() =>
            ShapeCards.Where(c => c.kind == CardKind.Drawing && !c.crumpled && c.strokes != null && c.strokes.Count > 0)
                      .OrderByDescending(c => c.kept).ThenByDescending(c => c.born).FirstOrDefault();

        private void ClosePolishQuestion(string why)
        {
            if (Question != null && !Question.answered && Question.who == Crew.Maker && Question.text == PolishLine) CloseQuestion(why);
        }

        // After the answer: the card's role, then "anything else?" (F79), as without the question.
        private void AfterPolish(ShapeCard card)
        {
            if (Current != Step.ShapeInput || card == null || !card.kept || card.crumpled) return;
            if (string.IsNullOrEmpty(card.role)) AskRole(card);
            else SayDoneLine();
        }

        // Maker makes every line of the card exact (F84 Perfect, in a Task) and the card's picture morphs into it.
        private IEnumerator PolishCard(ShapeCard card, bool redrawSketches)
        {
            if (card == null || card.strokes == null) yield break;
            polishing = true;
            var before = card.strokes.Select(s => new List<Vector2>(s)).ToList();
            var kinds = new string[before.Count];
            var job = Task.Run(() =>
            {
                var result = new List<List<Vector2>>();
                for (int i = 0; i < before.Count; i++) { result.Add(Perfect(before[i], out string kind)); kinds[i] = kind; }
                return result;
            });
            Body(Crew.Maker)?.Play("Idea_Pop");
            LookAt(Crew.Maker, card.Position, 1.6f);
            Ack(Crew.Maker, "Polishing!");
            while (!job.IsCompleted) yield return null;
            if (job.IsFaulted || job.Result == null)
            {
                polishing = false;
                Debug.LogWarning("[Windups Polish] " + job.Exception?.GetBaseException().Message);
                Say(Crew.Maker, "Hmm, I couldn't polish that one. I'll use it as drawn.");
                AfterPolish(card);
                yield break;
            }
            var after = job.Result;
            // The morph: a few in-between pictures, each drawn in a Task; the main thread only uploads them.
            var from = new List<List<Vector2>>();
            var to = new List<List<Vector2>>();
            for (int i = 0; i < before.Count; i++)
            {
                int n = Mathf.Clamp(Mathf.Max(before[i].Count, after[i].Count), 2, 200);
                from.Add(Resample(before[i], n));
                to.Add(Resample(after[i], n));
            }
            var image = card.root != null ? card.root.GetComponentInChildren<RawImage>(true) : null;
            var texture = new Texture2D(PolishSize, PolishSize, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp };
            owned.Add(texture);
            if (image != null) image.texture = texture;
            ToySounds.Play(ToySounds.Cue.Whoosh, card.Position, .4f, 1.4f);
            for (int f = 1; f <= PolishMorphFrames; f++)
            {
                float e = Mathf.SmoothStep(0f, 1f, f / (float)PolishMorphFrames);
                var frame = Task.Run(() =>
                {
                    var lines = new List<List<Vector2>>(from.Count);
                    for (int i = 0; i < from.Count; i++)
                    {
                        var line = new List<Vector2>(from[i].Count);
                        for (int k = 0; k < from[i].Count; k++) line.Add(Vector2.Lerp(from[i][k], to[i][k], e));
                        lines.Add(line);
                    }
                    return RasterPixels(f == PolishMorphFrames ? after : lines);
                });
                float until = Time.time + PolishMorphSeconds / PolishMorphFrames;
                while (!frame.IsCompleted) yield return null;
                if (frame.IsFaulted) break;
                texture.SetPixels32(frame.Result);
                texture.Apply(false);
                while (Time.time < until) yield return null;
            }
            if (!ShapeCards.Contains(card) || card.crumpled) { polishing = false; yield break; }
            byte[] oldJpeg = card.jpeg;
            card.jpeg = texture.EncodeToJPG(85);
            card.texture = texture;
            card.strokes = after;
            card.polished = true;
            card.hopAt = Time.time;
            if (DrawingJpeg == null || DrawingJpeg == oldJpeg) DrawingJpeg = card.jpeg;
            summaryToken++;
            Polishes++;
            ToySounds.Play(ToySounds.Cue.Chime, card.Position, .35f, 1.8f);
            ToyFx.Puff(card.Position, .006f, 4);
            Talk.Add("Maker polished the person's drawing: " + string.Join(", ", kinds));
            WriteInputLog("polished card " + card.id + ": " + string.Join(", ", kinds), true);
            polishing = false;
            if (redrawSketches && Current == Step.Looks && FormFirst)
            {
                Say(Crew.Maker, "There! Clean lines. Sketching it again.");
                StartCoroutine(MakeLooks("forms"));
                yield break;
            }
            Ack(Crew.Maker, "There! Clean lines.");
            AfterPolish(card);
        }

        // The drawing as pixels (RasterDrawing's look, pure arithmetic so it runs in a Task).
        private static Color32[] RasterPixels(List<List<Vector2>> strokes)
        {
            const int size = PolishSize;
            var pixels = new Color32[size * size];
            var paperColour = new Color32(253, 252, 245, 255);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = paperColour;
            var ink = new Color32(60, 64, 80, 255);
            foreach (var stroke in strokes)
                for (int i = 0; i < stroke.Count; i++)
                {
                    Vector2 a = Px(stroke[Mathf.Max(0, i - 1)]), b = Px(stroke[i]);
                    int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b)));
                    for (int k = 0; k <= steps; k++)
                    {
                        Vector2 q = Vector2.Lerp(a, b, k / (float)steps);
                        for (int dx = -2; dx <= 2; dx++)
                            for (int dy = -2; dy <= 2; dy++)
                            {
                                int x = Mathf.RoundToInt(q.x) + dx, y = Mathf.RoundToInt(q.y) + dy;
                                if (x >= 0 && y >= 0 && x < size && y < size && dx * dx + dy * dy <= 5) pixels[y * size + x] = ink;
                            }
                    }
                }
            return pixels;

            Vector2 Px(Vector2 v) => new Vector2((v.x / PaperSize + .5f) * (size - 1), (v.y / PaperSize + .5f) * (size - 1));
        }

        // Tests.
        public ShapeCard PolishTarget => polishCard;
        public bool Polishing => polishing;
    }
}
