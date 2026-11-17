using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Windups.Handoff
{
    // F63 (2026-11-13, after the Quest try with a heart-shaped coaster):
    //  - The person's shape rules: "no bumps", "make the bottom flat" hold for the rest of the make (ShapeRules). They
    //    go with every picture request (the shape sketches, the next version's redraw, the finishes), into the crew's
    //    workbench, and a crew flag against one is dropped. A rule also takes the detail it forbids out of Need for
    //    good: Spark's idea note said "a bumpy top" and that sentence went into every request, so all three sketches of
    //    a plain heart drawing came with a wavy floor, and v2 deepened it.
    //  - A shape wish while colouring is not paint (FinishShapeVoice). "Make the bottom flat", said three times over
    //    the finish pictures, only went into the vibe; finishes never change the shape and Meshy's retexture keeps it.
    //    Now Maker says so, draws the next version with that change at once (the usual picture check, stand and Keep
    //    this shape), and the vibe already said is drawn again on the new shape (ResumeVibe).
    //  - maker_ai_log.jsonl: what the picture jobs were asked and answered, and every picture request's words and the
    //    roles of its reference images (nothing showed why three sketches came back the same).
    public sealed partial class MakerSession
    {
        public const string BottomRule = "Flat, smooth bottom, inside and underneath: no bumps, ripples or waves";
        public const string InsideRule = "Smooth, flat inside floor where things sit: no bumps, ripples or waves";
        public const string UnderRule = "Flat underside, so it sits flat without rocking";
        public const string SmoothRule = "Smooth, plain surfaces: no bumps, ripples or waves";

        // The person's standing rules for this make's shape, oldest first.
        public readonly List<string> ShapeRules = new List<string>();
        public string VibeAfterShape => vibeAfterShape;
        public int FinishShapeChanges { get; private set; }
        public int FlagsDroppedByRules { get; private set; }
        public string LastPictureText { get; private set; } = "";
        public readonly Dictionary<string, string> LastAskInput = new Dictionary<string, string>();
        private string vibeAfterShape, aiLogPath;
        private int pictureLook;
        private static readonly object aiLogLock = new object();

        private void ResetShapeRules()
        {
            ShapeRules.Clear();
            vibeAfterShape = null;
        }

        // ------------------------------------------------------------ words

        private const RegexOptions RuleRx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private const string NotPaint = @"(?!\s+(?:colou?rs?|paint\w*|finish|look|shade|tone|matte|sheen|pink|red|blue|green|yellow|orange|purple|white|black|gr[ae]y|gold|silver)\b)";
        private static readonly Regex FlatWords = new Regex(@"\bflat(?:ter|ten\w*)?\b" + NotPaint + "|평평|매끈", RuleRx);
        private static readonly Regex ReliefWords = new Regex(@"\b(?:bump\w*|lump\w*|ripple\w*|dents?|dented|waves?|wavy)\b|울퉁", RuleRx);
        private static readonly Regex SizeShapeWords = new Regex(@"\b(?:taller|shorter|thicker|thinner|deeper|shallower|wider|narrower|rounder|pointier|higher|lower)\b|높게|낮게|두껍게|얇게|깊게|얕게", RuleRx);
        private static readonly Regex ShapePartWords = new Regex(@"\b(?:holes?|handles?|notch\w*|feet|legs?)\b|구멍|손잡이", RuleRx);
        private static readonly Regex DecorationWords = new Regex(@"\b(?:stripes?|lines?|patterns?|dots?|spots?|borders?|bands?|letters?|text|colou?rs?|paint\w*|glaze\w*|glossy|matte|shiny|brighter|darker|lighter|pink|red|blue|green|yellow|orange|purple|white|black|gr[ae]y|gold|silver)\b|색|칠", RuleRx);
        private static readonly Regex UndersideWords = new Regex(@"\b(?:underside|underneath|beneath|under\s+side|bottom\s+side|base|sits?\s+flat|stands?\s+flat|wobbl\w*|rock\w*|tips?\s+over)\b|밑면|흔들", RuleRx);
        private static readonly Regex InsideWords = new Regex(@"\b(?:inside|inner|floor|surface|middle)\b|안쪽|안에", RuleRx);
        private static readonly Regex BottomWords = new Regex(@"\bbottom\b|바닥", RuleRx);
        private static readonly Regex RemovalWords = new Regex(@"\b(?:no|not|without|remove\w*|get\s+rid|less|fewer|don'?t|do\s+not|flatten\w*|smooth\w*|stop|lose|drop)\b|없애|빼|말고", RuleRx);
        private static readonly Regex NoThing = new Regex(@"\b(?:no|without|remove|get\s+rid\s+of|take\s+(?:off|away)|lose|drop|don'?t\s+want|do\s+not\s+want)\s+(?:the\s+|any\s+|those\s+|these\s+|that\s+|this\s+|its\s+)?([a-z]{3,})", RuleRx);
        private static readonly string[] NotThings = { "more", "one", "ones", "it", "them", "that", "this", "colour", "color", "colours", "colors", "paint", "need", "idea", "ideas", "problem", "worries", "way", "change", "changes", "thanks" };
        private static readonly string[] ReliefStems = { "bump", "lump", "ripple", "wave", "wavy", "dent", "dimple" };

        // A wish about the made thing's shape (not its paint): relief words always; sizes and parts only when the
        // words are not about decoration ("thicker stripes", "paint the handle red" stay paint).
        public static bool ShapeWish(string p)
        {
            p = p ?? "";
            if (Flattening(p)) return true;
            return (SizeShapeWords.IsMatch(p) || ShapePartWords.IsMatch(p)) && !DecorationWords.IsMatch(p);
        }

        // "Flat", or bumps that are not wanted ("no bumps", "smooth out the ripples", "it's still too bumpy").
        private static bool Flattening(string p) => FlatWords.IsMatch(p) ||
            (ReliefWords.IsMatch(p) && (RemovalWords.IsMatch(p) || Regex.IsMatch(p, @"\b(?:too|still|so)\b", RuleRx)));

        // What of the person's words holds for the rest of the make (null: a one-time change, e.g. "taller").
        // "Bottom" on a dish, plate, tray or coaster means where things sit as much as the underside, so a plain
        // "bottom" makes both flat; "underneath"/"wobbles" is the underside only, "inside"/"floor"/"surface" the inside.
        public static string RuleFrom(string text, out string part)
        {
            part = null;
            string p = (text ?? "").ToLowerInvariant();
            if (Flattening(p))
            {
                bool under = UndersideWords.IsMatch(p), inside = InsideWords.IsMatch(p), bottom = BottomWords.IsMatch(p);
                part = "base";
                if (under && !inside) return UnderRule;
                if (inside && !under) { part = "inside"; return InsideRule; }
                if (bottom || (under && inside)) return BottomRule;
                part = "top";
                return SmoothRule;
            }
            var m = NoThing.Match(p);
            if (m.Success)
            {
                string thing = m.Groups[1].Value;
                if (Array.IndexOf(NotThings, thing) < 0 && !ReliefStems.Any(thing.StartsWith))
                {
                    part = PartFromWords(p);
                    return "No " + thing;
                }
            }
            return null;
        }

        // The words the rules forbid ("No handle" -> "handle"; the no-bumps rules -> bump, ripple, wave...).
        public static List<string> StemsFor(IEnumerable<string> rules)
        {
            var stems = new List<string>();
            foreach (var r in rules ?? Enumerable.Empty<string>())
            {
                if (r.Contains("no bumps")) stems.AddRange(ReliefStems);
                else if (r.StartsWith("No ", StringComparison.Ordinal))
                {
                    string t = r.Substring(3).Trim().ToLowerInvariant();
                    if (t.Length > 4 && t.EndsWith("s", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
                    if (t.Length > 2) stems.Add(t);
                }
            }
            return stems.Distinct().ToList();
        }

        private List<string> BannedStems() => StemsFor(ShapeRules);

        private static bool HasStem(string s, IEnumerable<string> stems) =>
            stems.Any(st => Regex.IsMatch(s ?? "", @"\b" + Regex.Escape(st), RuleRx));

        // A teammate's idea against a rule ("Keep the bumps, deepen them" with no bumps; "Smooth out the bumps" is fine).
        public static bool AgainstRules(string text, IList<string> stems) =>
            stems != null && stems.Count > 0 && HasStem(text, stems) && !RemovalWords.IsMatch(text ?? "");

        public bool AgainstRules(string text) => AgainstRules(text, BannedStems());

        // The clauses of a sentence that name a forbidden thing go: "Make a round coaster with a shallow raised lip
        // and a bumpy top in bright blue." -> "Make a round coaster with a shallow raised lip."
        public static string Scrub(string text, ICollection<string> stems)
        {
            if (string.IsNullOrWhiteSpace(text) || stems == null || stems.Count == 0) return text;
            var parts = Regex.Split(text, @"(?=\b(?:and|with|plus|but)\b)|(?<=[,;])", RuleRx);
            var keep = parts.Where(s => !HasStem(s, stems)).ToList();
            if (keep.Count == parts.Length) return text;
            string joined = string.Concat(keep).Trim().TrimEnd(',', ';', '.').Trim();
            joined = Regex.Replace(joined, @"\s*\b(?:and|with|plus|but)\s*$", "", RuleRx).Trim().TrimEnd(',', ';').Trim();
            joined = Regex.Replace(joined, @"^(?:and|with|plus|but)\s+", "", RuleRx);
            if (joined.Length == 0) return "";
            if (text.TrimEnd().EndsWith(".", StringComparison.Ordinal)) joined += ".";
            return joined;
        }

        // ------------------------------------------------------------ the rules

        // Called with whatever the person says about the shape (v2 flags, words over the sketches, a wish while
        // colouring). True when it became (or already was) a rule.
        private bool AddShapeRule(string text)
        {
            string rule = RuleFrom(text, out _);
            if (rule == null) return false;
            if (ShapeRules.Contains(rule) || ((rule == InsideRule || rule == UnderRule) && ShapeRules.Contains(BottomRule))) return true;
            if (rule == BottomRule) { ShapeRules.Remove(InsideRule); ShapeRules.Remove(UnderRule); }
            ShapeRules.Add(rule);
            string was = Need;
            string scrubbed = Scrub(Need, BannedStems());
            if (!string.IsNullOrWhiteSpace(scrubbed)) Need = scrubbed;
            Talk.Add("The person's rule for the shape: " + rule);
            Note("decision", "Your rule", rule);
            WriteInputLog("shape rule: " + rule + (Need != was ? " | need now: " + Need : ""), true);
            return true;
        }

        // For a picture prompt ("" without rules).
        private string RulesLine(string tail = "") => ShapeRules.Count == 0 ? "" :
            " The person's own rules for the shape (always follow them, they win over everything else" + tail + "): " + string.Join("; ", ShapeRules) + ".";

        // ------------------------------------------------------------ a shape wish while colouring

        private bool Colouring => FormFirst && Product != null && ShapeConfirmed && Review == ReviewStage.None &&
            (Current == Step.Finish || (Current == Step.Looks && lookJob == "finishes") || (Current == Step.Done && finishDone));

        // Paint cannot change the shape: the next version is made with the wish (F77: straight away, then Start
        // colouring), and the vibe said before comes back on it (ResumeVibe).
        private bool FinishShapeVoice(string text, string p)
        {
            if (!Colouring || !ShapeWish(p)) return false;
            string rule = RuleFrom(text, out string part);
            string change = rule;
            if (rule != null) AddShapeRule(text);
            else
            {
                change = MakerRoles.Clean(text).Trim().TrimEnd('.', '!');
                part = PartFromWords(p);
            }
            vibeAfterShape = string.IsNullOrWhiteSpace(VibeWords) ? null : VibeWords;
            FinishShapeChanges++;
            if (Current == Step.Looks) ClearLooks();
            StopLessons();
            WriteInputLog("colouring -> shape: " + change + (vibeAfterShape != null ? " | vibe kept: " + vibeAfterShape : ""), true);
            Talk.Add("Maker: paint can't change the shape, so the next version comes first");
            crewFlagsFor = Product; // the person said what to change: no crew flags this time
            EnterShapeReview();
            if (Review != ReviewStage.Shape) return true;
            EnterChangeMode(false);
            PersonFlag(change, part, true);
            if (HasShapeChanges) StartCoroutine(PreviewRevision()); // F77: made straight away, no picture to approve
            Say(Crew.Maker, "Paint can't change the shape, so I'm making that first." + (vibeAfterShape != null ? " Then your colours again." : ""));
            return true;
        }

        // StartFinish after a shape changed while colouring: the vibe already said, drawn again on the new shape.
        private bool ResumeVibe()
        {
            string vibe = vibeAfterShape;
            vibeAfterShape = null;
            if (string.IsNullOrWhiteSpace(vibe) || !CanRepaint) return false;
            Current = Step.Finish;
            StopLessons();
            vibeOffered = true;
            VibeWords = vibe;
            Talk.Add("The vibe again on the new shape: " + vibe);
            WriteInputLog("vibe again on the new shape: " + vibe, true);
            Say(Crew.Maker, "Shape kept! Now your colours on it again.");
            StartCoroutine(MakeLooks("finishes"));
            return true;
        }

        // ------------------------------------------------------------ the picture requests (MakerSession.FormFirst.cs)

        // Draw: which of the three pictures is asked for (look 1 copies the person's whole-shape picture, looks 2
        // and 3 keep only its outline).
        private UnityWebRequest PictureRequestFor(string prompt, int look)
        {
            pictureLook = look;
            try { return PictureRequest(prompt); }
            finally { pictureLook = 0; }
        }

        // The person's picture look 1 copies: the one whole-shape picture, else a lone picture with no role, else the
        // drawing given outside the cards.
        private byte[] ExactShapePicture(List<ShapeCard> cards)
        {
            var whole = cards.Where(c => c.role == WholeShape).ToList();
            if (whole.Count == 1) return whole[0].jpeg;
            if (whole.Count == 0 && cards.Count == 1 && string.IsNullOrEmpty(cards[0].role)) return cards[0].jpeg;
            if (cards.Count == 0) return DrawingJpeg;
            return null;
        }

        // ------------------------------------------------------------ the AI log

        private static readonly string[] LoggedJobs = { "ideas", "forms", "looks", "finishes", "card" };

        private void NoteAsk(string job, object input)
        {
            try { LastAskInput[job] = JsonConvert.SerializeObject(input); }
            catch (Exception exception) { LastAskInput[job] = ""; Debug.LogWarning("[Windups F63] " + exception.Message); }
        }

        private void LogAsk(string job, string reply, int images)
        {
            if (Array.IndexOf(LoggedJobs, job) < 0) return;
            LastAskInput.TryGetValue(job, out string asked);
            LogAi(new { t = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), job, images, asked, reply });
        }

        private void LogPicture(int look, string prompt, List<(byte[] jpeg, string role)> refs)
        {
            var roles = refs.Select(r => r.role).ToArray();
            LastPictureText = prompt + " || " + string.Join(" | ", roles);
            LogAi(new { t = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), job = "picture " + lookJob, look = look + 1, prompt, refs = roles });
        }

        private void LogAi(object entry)
        {
            aiLogPath ??= Path.Combine(Application.persistentDataPath, "maker_ai_log.jsonl"); // (main thread only)
            string path = aiLogPath, line;
            try { line = JsonConvert.SerializeObject(entry) + "\n"; }
            catch (Exception exception) { Debug.LogWarning("[Windups F63] " + exception.Message); return; }
            Task.Run(() =>
            {
                try
                {
                    lock (aiLogLock)
                    {
                        var file = new FileInfo(path);
                        if (file.Exists && file.Length > 3_000_000) file.Delete();
                        File.AppendAllText(path, line);
                    }
                }
                catch { }
            });
        }

        // ------------------------------------------------------------ tests

        // What a picture of this round would be asked (its words and its reference images' roles), without sending it.
        public string DescribePicture(string job, string prompt, int look)
        {
            string was = lookJob;
            lookJob = job;
            try { using (PictureRequestFor(prompt, look)) { } return LastPictureText; }
            finally { lookJob = was; }
        }

        // A picture for the shape put on the note with its role (the frame and the role question are other checks').
        public ShapeCard SimulatePictureOnNote(byte[] jpeg, string role)
        {
            if (Current != Step.ShapeInput || jpeg == null) return null;
            Vector3 at = NotePoint + Vector3.up * .05f;
            var card = AddPhotoCard(jpeg, null, "", "test", at, at);
            if (card == null) return null;
            card.role = role ?? "";
            card.roleRaw = role ?? "";
            KeepCard(card);
            return card;
        }

        public void SimulateNeed(string need) => Need = need ?? "";
    }
}
