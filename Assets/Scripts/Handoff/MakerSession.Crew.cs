using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F29 Thinkmade workbench, the crew layer. The steps of MakerSession stay the spine (what to make,
    // vibe and looks, measure, shape and fit, make, print); on top of that the crew acts by itself:
    //  - Anything that is a question, a comparison or an opinion request goes to the whole crew. The AI
    //    ("turn") picks 1-3 actions, and the app performs them in the room: a spoken answer or question,
    //    a sticker of opinion on a look (like / worry / idea, in the speaker's colour), a vote where each
    //    teammate walks to stand under the look it prefers, pros and cons tabs on a look or the object,
    //    Spark looking around, or "do" (an instruction, carried out by the step logic).
    //  - When the looks appear, each teammate sticks its opinion on one (they may disagree).
    //  - When the person is quiet at a step that waits for them, one teammate takes the initiative
    //    (at most twice per step, never more often than every 30 s).
    //  - A returning person is welcomed without the introductions: Maker asks what to make today,
    //    or the crew asks how the last make worked out, or Spark comes back with something it found.
    public sealed partial class MakerSession
    {
        public sealed class Sticker
        {
            public Crew who; public string mood; public Picture picture; public Transform root;
            // F47: the few words printed on it and the teammate's whole line (a tap opens it as a note).
            public string tag = "", line = "";
            // Its shape in its own space (the disc at the origin, the words strip along x): for taps.
            public float radius, stripFrom, stripTo, stripHalf;
            public float side;   // +1: its words run to the right (a left corner), -1: to the left
            public bool top;     // on the picture's top edge (its note opens below it), else the bottom
        }
        public readonly List<Sticker> Stickers = new List<Sticker>();
        public readonly List<Transform> Tabs = new List<Transform>();
        public readonly List<string> ActionLog = new List<string>();     // "event:who:act:look:mood"
        public readonly Dictionary<Crew, int> VotedFor = new Dictionary<Crew, int>();
        public int Turns { get; private set; }
        public int Nudges { get; private set; }
        public int CallOvers { get; private set; }
        public string Greeting { get; private set; } = "";
        public bool Comparing { get; private set; }
        public bool CrewBusy => crewBusy;
        // Seconds of quiet before a teammate takes the initiative (tests shorten it).
        public static float IdleNudgeAfter = 35f;
        // Tests: force the welcome ("today", "fit", "found").
        public static string ForceGreeting;

        private float lastHeardAt = -100f, lastLineAt = -100f, lastStepAt, nextNudgeAt;
        private Step nudgeStep = (Step)(-1);
        private int nudgesInStep;
        private bool crewBusy;
        private Step tabsStep;
        private float tabsUntil;
        private readonly Dictionary<string, Mesh> stickerMeshes = new Dictionary<string, Mesh>();

        private void Heard() => lastHeardAt = Time.time;

        private void ResetCrew()
        {
            CloseStickerNote("reset"); // F47
            Stickers.Clear();
            ClearTabs();
            VotedFor.Clear();
            Comparing = false;
            crewBusy = false;
            nudgesInStep = 0;
            queuedQuestions.Clear();
            waiting = null; // F54
            CloseQuestion("reset"); // F44
        }

        // ------------------------------------------------------------ what goes to the crew

        private static readonly string[] CrewWords =
        {
            "compare", "versus", " vs", "which one", "which is better", "which do you", "pros", "cons", "what do you think",
            "what do you guys", "thoughts", "opinion", "do you like", "would you", "should i", "should we", "why ", "any idea",
            "what else", "how about", "what about", "is it good", "is this good", "does it work",
            "비교", "뭐가 나아", "어느 게", "어떤 게 나", "장단점", "장점", "단점", "어때", "어떻게 생각", "의견", "왜", "추천", "괜찮", "뭐가 좋"
        };

        public static bool AsksCrew(string text, string p) =>
            CrewWords.Any(p.Contains) || (text ?? "").TrimEnd().EndsWith("?");

        // What the crew knows when it decides (short; the talk carries the rest).
        private object Workbench() => new
        {
            step = Current.ToString(),
                review = Review.ToString(),
                shapeConfirmed = ShapeConfirmed,
                shapeRequirements = ShapeRequirements,
                personRules = ShapeRules.ToArray(), // F63
                workflow = ShapeWorkflow, // F77
            need = Need,
            forObject = TargetName ?? Target?.seen.name ?? "",
            style = Style,
            looks = ShownLooks().Select((l, i) => new { index = i, label = l.look?.label ?? "", prompt = l.look?.prompt ?? "" }).ToArray(),
            chosenLook = Look?.label ?? "",
            measuredMm = HasMeasure ? Measured : 0f,
            shape = Shape,
            design = Design != null ? Design.Summary : "",
            gapMm = Design != null ? Gap : 0f,
            made = Product != null && Current == Step.Done ? Design?.name ?? "" : Kept != null ? "last time: " + Kept.name : "",
            material = Material,
            palette = Palette.Skip(Mathf.Max(0, Palette.Count - 6)).Select(s => (s.kind == "texture" ? "texture of " : "") + s.name + " " + s.hex).ToArray(),
            handSizeMm = HandSizeMm,
            drawnByPerson = DrawnOutline != null || DrawnProfile != null ? DrawMode : "",
            versions = Versions.Where(v => v.root != null).Select(v => "v" + v.number + ": " + v.design?.Summary).ToArray(),
            // F40: the see-through v2 and the flags the person planted on it or set aside.
            draft = Draft != null ? new
            {
                planted = Flags.Where(f => f.place == FlagPlace.Planted).Select(f => f.text).ToArray(),
                setAside = Flags.Where(f => f.place == FlagPlace.Parked).Select(f => f.text).ToArray(),
                sizeMm = DraftScale != Vector3.one ? DraftSizeMm.x.ToString("0") + " x " + DraftSizeMm.z.ToString("0") + " x " + DraftSizeMm.y.ToString("0") : ""
            } : null,
            kind = Kind,
            ideaNotes = Ideas.Where(c => c.root != null).Select((c, i) => new { index = i, label = c.idea.label, by = MakerRoles.Name(c.who), kind = c.idea.kind }).ToArray(),
            decisions = Notes.Where(n => n.project == project).Select(n => n.title + ": " + n.text).TakeLast(8).ToArray()
        };

        private List<Picture> ShownLooks() => Looks.Where(l => l.root != null && l.root.gameObject.activeInHierarchy && l.Ready).ToList();

        // ------------------------------------------------------------ a crew turn

        private IEnumerator CrewTurn(string kind, string said)
        {
            // (F40: the flags after a make are never skipped: v2 is drafted with them.)
            if (crewBusy && kind != "said" && kind != "made") yield break;
            var startedStep = Current;
            int startedEpoch = reviewEpoch;
            crewBusy = true;
            if (kind == "looks_shown") ForgetRecommendation(); // F61
            Turns++;
            string json = null;
            if (Scripted)
            {
                yield return new WaitForSeconds(.35f);
                json = MakerRoles.ScriptedTurn(kind, said, Mathf.Max(ShownLooks().Count, VoteTargets().Count), Design != null);
            }
            else
            {
                // F80 (Quest: "compare is too slow"): while the comparison is being thought out all three think, looking at
                // the pictures, so the wait shows at once.
                bool comparing = kind == "said" && (said ?? "").ToLowerInvariant().Contains("compare");
                if (kind == "said") Cue(Crew.Maker, "thinking");
                if (comparing) { Cue(Crew.Spark, "thinking"); Cue(Crew.Fixer, "thinking"); }
                yield return Ask(Crew.Maker, "turn", new { workbench = Workbench(), @event = new { type = kind, said = said ?? "" }, talk = Talk.Skip(Mathf.Max(0, Talk.Count - 14)).ToArray() }, r => json = r, false);
                Cue(Crew.Maker, "idle");
                if (comparing) { Cue(Crew.Spark, "idle"); Cue(Crew.Fixer, "idle"); }
            }
            TurnReply reply = null;
            if (json != null)
                try { reply = JsonConvert.DeserializeObject<TurnReply>(json); }
                catch (Exception exception) { Debug.LogWarning("[Windups Crew] " + exception.Message); }
            var actions = (reply?.actions ?? new CrewAction[0]).Where(a => a != null).Take(4).ToList(); // F77: compare = 3 votes + the winner's pros and cons
            bool performs = startedEpoch == reviewEpoch && Current == startedStep && actions.Count > 0;
            // F103 (Quest 2026-11-16: "no flags at all this time", and nothing in the logs said why): the made turn says
            // what came back and what became of it.
            if (kind == "made")
                WriteInputLog("crew turn made | " + (json == null ? "no reply" : actions.Count + " actions: " + string.Join(", ", actions.Select(a => a.who + " " + a.act))) +
                              (performs ? "" : json == null ? "" : " | not performed (step " + startedStep + " -> " + Current + (startedEpoch != reviewEpoch ? ", review moved on" : "") + ")"), true);
            int flagsBefore = FlagPops;
            if (performs) yield return Perform(actions, kind);
            if (kind == "made" && performs) WriteInputLog("crew turn made | flags offered " + (FlagPops - flagsBefore), true);
            crewBusy = false;
            // F54: Maker's "Pick a picture" waits while a teammate's question is open: the answer starts another turn,
            // and Maker says it at the end of that one (it used to push the teammate's question and its pills away).
            if (startedEpoch == reviewEpoch && Current == startedStep && Current == Step.Looks && LooksShown && !CrewQuestionOpen)
            {
                // F61: one line instead of the three stickers read back: the one Maker would pick, and why.
                string pick = kind == "looks_shown" ? Recommendation() : "";
                // F77 (the user: "only Compare them, doing what both did"): the three vote by walking under the one they
                // like, then the pros and cons of the winner stand on each side of it.
                AskWith(Crew.Maker, (pick.Length > 0 ? pick + " " : "") + "Pick a picture, or tap a sticker to read it.", false,
                    Rp(CompareLabel, CompareSay));
            }
        }

        public const string CompareLabel = "Compare them", CompareSay = "Compare them, which one is best?";

        private IEnumerator Perform(List<CrewAction> actions, string kind)
        {
            var looks = ShownLooks();
            var targets = VoteTargets();
            var votes = new List<(Crew crew, Transform target, string label, string say)>();
            // F77: in a comparison the pros and cons come after the vote, on the picture most of them chose.
            bool comparing = actions.Any(x => x.act == "vote");
            var afterVote = new List<(Crew crew, CrewAction action, string say)>();
            foreach (var a in actions)
            {
                if (!Enum.TryParse(a.who, out Crew crew)) crew = Crew.Maker;
                // F103: after a make each teammate offers a flag; an idea the turn gave as a plain line (or a question) is
                // planted as that teammate's flag all the same (the see-through v2 is waiting for them).
                if (kind == "made" && (a.act == "say" || a.act == "ask") && !string.IsNullOrWhiteSpace(a.say)) a.act = "flag";
                string say = MakerRoles.Clean(a.say);
                var picture = a.look >= 0 && a.look < looks.Count ? looks[a.look] : null;
                var target = a.look >= 0 && a.look < targets.Count ? targets[a.look] : default;
                ActionLog.Add(kind + ":" + crew + ":" + a.act + ":" + a.look + ":" + a.mood);
                if (ActionLog.Count > 200) ActionLog.RemoveAt(0);
                switch (a.act)
                {
                    case "sticker":
                        if (picture != null) StickOn(crew, picture, a.mood, a.tag, say);
                        Gesture(crew, a.mood);
                        // F61: a sticker on fresh pictures is silent (its sentence opens when it is tapped).
                        if (kind == "looks_shown" && picture != null) QuietSticker(crew, picture, say);
                        else SayAbout(crew, say, picture?.root); // F76
                        break;
                    case "vote":
                        if (target.root != null && votes.All(v => v.crew != crew)) votes.Add((crew, target.root, target.label, say));
                        else Say(crew, say);
                        break;
                    case "flag":
                        // F63: never a flag against the person's own rule ("Keep the bumps, deepen them" after "no bumps").
                        if (AgainstRules(say)) { FlagsDroppedByRules++; WriteInputLog("flag dropped (against the person's rule): " + MakerRoles.Name(crew) + ": " + say, true); break; }
                        if (Product != null && Current == Step.Done) PlantFlag(crew, string.IsNullOrWhiteSpace(a.part) ? "rim" : a.part, say);
                        Gesture(crew, "idea");
                        Say(crew, say);
                        break;
                    case "pros_cons":
                        if (comparing) { afterVote.Add((crew, a, say)); continue; }
                        ProsConsAction(crew, picture, a, say);
                        break;
                    case "look_around":
                        Say(Crew.Spark, say);
                        // F45: only when the person asked (said or tapped), never on the crew's own (idle) turn.
                        if (Current != Step.Making && kind == "said") LookAroundNow();
                        break;
                    case "do":
                        if (!string.IsNullOrWhiteSpace(say) && kind == "said" && Question == null && Review == ReviewStage.None)
                        {
                            Talk.Add("Maker takes it as: " + say);
                            StepInput(say, say.ToLowerInvariant().Trim().TrimEnd('.', '!', '?'));
                        }
                        break;
                    case "ask":
                        Body(crew)?.Play("HeadTilt");
                        AskCrewQuestion(crew, say, a.options);
                        break;
                    default:
                        Gesture(crew, a.mood);
                        // (F44: a line that asks something keeps its card up with the replies to tap.)
                        if (say.TrimEnd().EndsWith("?")) AskCrewQuestion(crew, say, a.options); else Say(crew, say);
                        break;
                }
                if (a.act != "vote") yield return new WaitForSeconds(Scripted ? .12f : .3f); // F80: votes go together
            }
            if (votes.Count > 0) yield return Vote(votes);
            if (afterVote.Count == 0) yield break;
            // The winner: the picture most teammates stood under (the first vote breaks a tie).
            Transform winner = votes.GroupBy(v => v.target).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            int won = winner != null ? targets.FindIndex(t => t.root == winner) : -1;
            var (who, action, line) = afterVote[0];
            int index = won >= 0 ? won : action.look;
            var shown = ShownLooks();
            ProsConsAction(who, index >= 0 && index < shown.Count ? shown[index] : null, action, line);
            ComparedWinner = index;
        }

        public int ComparedWinner { get; private set; } = -1;

        private void ProsConsAction(Crew crew, Picture picture, CrewAction a, string say)
        {
            ShowProsCons(crew, picture, a.pros, a.cons);
            Gesture(crew, "idea");
            SayAbout(crew, say, picture?.root); // F76
            Note("decision", "Pros and cons" + (picture?.look != null ? " of " + MakerRoles.Clean(picture.look.label) : ""),
                "+ " + string.Join(", ", (a.pros ?? new string[0]).Select(MakerRoles.Clean)) + "  - " + string.Join(", ", (a.cons ?? new string[0]).Select(MakerRoles.Clean)));
        }

        // F44: the crew's own question, with the AI's short answers as reply pills (answered back to the crew).
        private void AskCrewQuestion(Crew crew, string say, string[] options)
        {
            var replies = (options ?? new string[0]).Select(MakerRoles.Clean).Where(o => o.Length > 0 && o.Length <= 40).Distinct().Take(3).Select(o => new Reply(o, o)).ToArray();
            AskWith(crew, say, true, replies);
        }

        private WindupBody Body(Crew crew) => cast.TryGetValue(crew, out var c) && c != null ? WindupBody.Of(c) : null;

        private void Gesture(Crew crew, string mood)
        {
            var body = Body(crew);
            if (body == null) return;
            if (mood == "like") body.Play("React_Nod");
            else if (mood == "worry") body.Play("React_Shake");
            else if (mood == "idea") body.Play("Idea_Pop");
        }

        // ------------------------------------------------------------ stickers on the looks

        private void StickOn(Crew crew, Picture picture, string mood, string tag = "", string line = "")
        {
            if (picture?.root == null) return;
            foreach (var old in Stickers.Where(s => s.who == crew && s.picture == picture).ToList())
            {
                if (old == NoteSticker) CloseStickerNote("replaced"); // F47
                if (old.root != null) Destroy(old.root.gameObject);
                Stickers.Remove(old);
            }
            // Each teammate has its own corner, so several stickers never cover each other; its words run
            // inward along the edge (F47: Fixer a little higher so they stay off the picture's name).
            // F52 (the user: "everything listed under the pictures looks bad"): back on the picture's corners (F47's
            // chosen design A) instead of F50's rows under it: Spark top-left, Maker top-right, Fixer bottom-right.
            float edge = picture.size * .5f - LookStickerRadius * .35f;
            Vector3 corner = crew == Crew.Spark ? new Vector3(-edge, edge, -.012f)
                : crew == Crew.Maker ? new Vector3(edge, edge, -.012f)
                : new Vector3(edge, -picture.size * .42f, -.012f);
            var sticker = new Sticker { who = crew, mood = mood, picture = picture, tag = StickerTag(tag), line = MakerRoles.Clean(line), side = corner.x > 0f ? -1f : 1f, top = corner.y > 0f };
            sticker.root = BuildSticker(crew, mood);
            BuildStickerWords(sticker, picture.size); // F47 (MakerSession.StickerNotes.cs)
            Stickers.Add(sticker);
            Vector3 from = cast.TryGetValue(crew, out var c) && c != null ? c.transform.position + Vector3.up * .16f : picture.root.position;
            // A sticker with words lies almost straight along the edge (a strong tilt would cross the picture).
            float tilt = 0f;
            StartCoroutine(FlySticker(sticker, from, corner, tilt));
        }

        private IEnumerator FlySticker(Sticker sticker, Vector3 from, Vector3 corner, float tilt)
        {
            var root = sticker.root;
            root.position = from;
            ToySounds.Play(ToySounds.Cue.Pop, from, .35f, 1.5f);
            for (float t = 0f; t < 1f && root != null && sticker.picture.root != null; t += Time.deltaTime / .45f)
            {
                Vector3 to = sticker.picture.root.TransformPoint(corner);
                Vector3 at = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t)) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .05f;
                root.SetPositionAndRotation(at, sticker.picture.root.rotation * Quaternion.Euler(0f, 0f, tilt * t + 360f * (1f - t)));
                yield return null;
            }
            // F63: its picture went while it flew (picked, redrawn, cleared): the sticker goes too (it used to stay in the air).
            if (root == null || sticker.picture.root == null)
            {
                if (root != null) Destroy(root.gameObject);
                Stickers.Remove(sticker);
                yield break;
            }
            root.SetParent(sticker.picture.root, false);
            root.localPosition = corner;
            root.localRotation = Quaternion.Euler(0f, 0f, tilt);
            root.localScale = Vector3.one * 1.25f;
            for (float t = 0f; t < 1f && root != null; t += Time.deltaTime / .2f) { root.localScale = Vector3.one * Mathf.Lerp(1.25f, 1f, t); yield return null; }
            ToySounds.Play(ToySounds.Cue.Press, root != null ? root.position : from, .4f, 1.7f);
        }

        // A round sticker in the teammate's colour with a white mark: a heart (like), "!" (worry) or a bulb (idea).
        private Transform BuildSticker(Crew crew, string mood)
        {
            var root = new GameObject("Sticker " + crew + " " + mood).transform;
            root.SetParent(transform, false);
            Color color = MakerRoles.Color(crew), white = new Color(.98f, .98f, .96f);
            Part(root, "Disc", StickerMesh("disc" + crew, () => ToyMeshes.Disc(LookStickerRadius, .0016f, color, Color.Lerp(color, Color.black, .25f), "StickerDisc")), Vector3.zero, 0f);
            // F47: a white die-cut edge, as on a real sticker sheet.
            Part(root, "Cut", StickerMesh("cut", () => ToyMeshes.Disc(LookStickerRadius + .0022f, .0012f, Color.white, new Color(.86f, .86f, .88f), "StickerCut")), new Vector3(0f, 0f, .0006f), 0f);
            Mesh dot = StickerMesh("dot", () => ToyMeshes.Disc(.0042f, .0008f, white, white, "StickerDot"));
            Mesh bar = StickerMesh("bar", () => ToyMeshes.RoundedSlab(.0045f, .011f, .0022f, .0008f, white, white, white, "StickerBar"));
            Mesh square = StickerMesh("square", () => ToyMeshes.RoundedSlab(.0075f, .0075f, .0012f, .0008f, white, white, white, "StickerSquare"));
            if (mood == "worry")
            {
                Part(root, "Mark", bar, new Vector3(0f, .0028f, -.001f), 0f);
                Part(root, "Mark", StickerMesh("dotSmall", () => ToyMeshes.Disc(.0025f, .0008f, white, white, "StickerDotSmall")), new Vector3(0f, -.0062f, -.001f), 0f);
            }
            else if (mood == "idea")
            {
                Part(root, "Mark", StickerMesh("bulb", () => ToyMeshes.Disc(.0058f, .0008f, white, white, "StickerBulb")), new Vector3(0f, .0022f, -.001f), 0f);
                Part(root, "Mark", StickerMesh("base", () => ToyMeshes.RoundedSlab(.0045f, .0035f, .0008f, .0008f, white, white, white, "StickerBase")), new Vector3(0f, -.0052f, -.001f), 0f);
            }
            else
            {
                // A heart: two dots and a square turned 45 degrees.
                Part(root, "Mark", dot, new Vector3(-.0029f, .0018f, -.001f), 0f);
                Part(root, "Mark", dot, new Vector3(.0029f, .0018f, -.001f), 0f);
                Part(root, "Mark", square, new Vector3(0f, -.0012f, -.0011f), 45f);
            }
            return root;
        }

        private Mesh StickerMesh(string key, Func<Mesh> make)
        {
            if (stickerMeshes.TryGetValue(key, out var mesh) && mesh != null) return mesh;
            mesh = make();
            owned.Add(mesh);
            stickerMeshes[key] = mesh;
            return mesh;
        }

        private void Part(Transform parent, string name, Mesh mesh, Vector3 at, float turn)
        {
            var part = Part(name, mesh);
            part.SetParent(parent, false);
            part.localPosition = at;
            part.localRotation = Quaternion.Euler(0f, 0f, turn);
        }

        // ------------------------------------------------------------ vote: stand under the one you like

        // What a vote can be about: the looks on show, or else the versions of the made object.
        private List<(Transform root, string label)> VoteTargets()
        {
            // F30: the idea notes stuck around the room, while they are up.
            if (Current == Step.Suggesting && Ideas.Any(c => c.root != null))
                return Ideas.Where(c => c.root != null).Select(c => (c.root, MakerRoles.Clean(c.idea.label))).ToList();
            var looks = ShownLooks();
            if (looks.Count > 0) return looks.Select(l => (l.root, l.look?.label ?? "look")).ToList();
            return Versions.Where(v => v.root != null).Select(v => (v.root, "v" + v.number)).ToList();
        }

        private readonly List<Vector3> claimedVoteSpots = new List<Vector3>();

        // F80 (Quest 2026-11-14: "Compare is too slow, and I can't tell what they are looking at while they talk; the
        // characters shouldn't all rush over there - highlight the picture being talked about instead of the bubble"):
        // the crew votes from where it stands. Each turns to the picture it picks, presents it (Decision_Present) and a
        // dot in its colour flies from its head onto that picture's lower edge (the votes stay visible); then each says
        // why while its picture is highlighted (MakerSession.Calm.cs: much bigger, the others smaller and dimmer, a
        // glowing frame on the picture, not the bubble). No walking there and back. CompareWalks = true restores F29's
        // walk (older checks only).
        public static bool CompareWalks;
        public const float VoteDotRadius = .0085f, VoteDotStagger = .22f;
        public int VoteDotsShown { get; private set; }
        private readonly List<Transform> voteDots = new List<Transform>();

        private IEnumerator VoteInPlace(List<(Crew crew, Transform target, string label, string say)> votes)
        {
            Comparing = true;
            VotedFor.Clear();
            foreach (var d in voteDots) if (d != null) Destroy(d.gameObject);
            voteDots.Clear();
            var targets = VoteTargets();
            var taken = new Dictionary<Transform, int>();
            int order = 0;
            foreach (var (crew, target, _, _) in votes)
            {
                if (!cast.TryGetValue(crew, out var c) || c == null || target == null) continue;
                VotedFor[crew] = targets.FindIndex(x => x.root == target);
                c.lookTarget = target;
                taken.TryGetValue(target, out int already);
                taken[target] = already + 1;
                StartCoroutine(VoteDot(crew, c, target, already, order++ * VoteDotStagger));
            }
            ToySounds.Play(ToySounds.Cue.Whoosh, Head != null ? Head.position + Head.forward * .5f : transform.position, .3f, 1.5f);
            yield return new WaitForSeconds(Scripted ? .3f : order * VoteDotStagger + .5f);
            foreach (var (crew, voted, _, say) in votes) SayAbout(crew, say, voted); // the picture it voted for is highlighted while it says why
            var favourite = taken.OrderByDescending(t => t.Value).Select(t => t.Key).FirstOrDefault();
            if (favourite != null)
            {
                StartCoroutine(Bump(favourite));
                Note("decision", "Crew vote", string.Join(", ", votes.Select(v => MakerRoles.Name(v.crew) + ": " + MakerRoles.Clean(v.label))));
            }
            float until = Time.time + 16f;
            yield return new WaitForSeconds(.5f);
            while (Time.time < until && (lines.Count > 0 || Voice.IsSpeaking || Voice.Loading)) yield return null;
            Comparing = false;
        }

        // A dot in the teammate's colour pops off its head and lands on the picture's lower edge (side by side when
        // several chose the same one).
        private IEnumerator VoteDot(Crew crew, WindupCharacter c, Transform target, int slot, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            if (target == null || c == null) yield break;
            WindupBody.Of(c)?.Play("Decision_Present");
            Color colour = MakerRoles.Color(crew);
            var dot = new GameObject("Vote " + crew).transform;
            dot.SetParent(target, false);
            Part(dot, "Ring", StickerMesh("voteRing", () => ToyMeshes.Disc(VoteDotRadius + .0022f, .0012f, Color.white, Color.white, "VoteRing")), new Vector3(0f, 0f, .0004f), 0f);
            Part(dot, "Dot", StickerMesh("voteDot" + crew, () => ToyMeshes.Disc(VoteDotRadius, .0016f, colour, colour * .85f, "VoteDot")), Vector3.zero, 0f);
            voteDots.Add(dot);
            VoteDotsShown++;
            float size = .16f;
            foreach (var p in Looks) if (p != null && p.root == target) size = p.size;
            foreach (var i in Ideas) if (i != null && i.root == target) size = i.height;
            // In the lower-left corner, a row going right (the name is under the picture, Fixer's sticker lower right).
            Vector3 land = new Vector3(-size * .5f + VoteDotRadius * 1.9f + slot * VoteDotRadius * 2.6f, -size * .42f, -.014f);
            Vector3 from = target.InverseTransformPoint(c.transform.position + Vector3.up * .17f);
            for (float t = 0f; t < 1f; t += Time.deltaTime / .45f)
            {
                if (dot == null) yield break;
                float e = Mathf.SmoothStep(0f, 1f, t);
                dot.localPosition = Vector3.Lerp(from, land, e) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .05f;
                dot.localScale = Vector3.one * Mathf.Lerp(.4f, 1f, e);
                dot.localRotation = Quaternion.identity;
                yield return null;
            }
            if (dot == null) yield break;
            dot.localPosition = land;
            dot.localScale = Vector3.one;
            ToySounds.Play(ToySounds.Cue.Pop, dot.position, .35f, 1.4f + slot * .15f);
        }

        private IEnumerator Vote(List<(Crew crew, Transform target, string label, string say)> votes)
        {
            if (!CompareWalks) { yield return VoteInPlace(votes); yield break; }
            Comparing = true;
            VotedFor.Clear();
            var room = arrival.handoff.meeting.environment;
            var homes = new Dictionary<Crew, Vector3>();
            var walkers = new List<RoomCapsule.CapsulePetController>();
            var targets = VoteTargets();
            var taken = new Dictionary<Transform, int>();
            claimedVoteSpots.Clear();
            foreach (var (crew, target, _, _) in votes)
            {
                if (!cast.TryGetValue(crew, out var c) || c == null || target == null) continue;
                VotedFor[crew] = targets.FindIndex(x => x.root == target);
                var pet = c.movement;
                homes[crew] = pet.transform.position;
                c.lookTarget = target;
                taken.TryGetValue(target, out int already);
                taken[target] = already + 1;
                // Under (or next to) it, on the crew's own surface, a little toward the person; side by side if shared.
                Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - target.position, Vector3.up).normalized : Vector3.back;
                Vector3 side = Vector3.Cross(Vector3.up, toUser);
                Vector3 under = new Vector3(target.position.x, pet.transform.position.y, target.position.z) + toUser * .07f + side * (already == 0 ? 0f : already == 1 ? .12f : -.12f);
                // Never closer than the crew's personal space to another voter or to someone who stays put
                // (2026-11-11: two voters for one picture stood 5.5 cm apart, heads overlapping).
                var others = new List<Vector3>(claimedVoteSpots);
                foreach (var o in cast)
                    if (o.Key != crew && o.Value != null && !votes.Any(v => v.crew == o.Key)) others.Add(o.Value.movement.transform.position);
                // F51: and clear of everything on the desk (things made, notes, the mic, the ring).
                Vector3 goal = room != null && Head != null && FreeStand(crew, under, .3f, out var free, claimedVoteSpots, false) ? free
                    : room != null && Head != null && GatherFormation.FreeSpotNear(room, Head.position, under, others, .3f, out free) ? free
                    : room != null && room.TrySameLevelNavPoint(under, out var snapped) ? snapped : under;
                claimedVoteSpots.Add(goal);
                if (Vector3.ProjectOnPlane(goal - pet.transform.position, Vector3.up).magnitude < .04f) continue; // already there
                pet.UnlockMeetingSeat();
                walkers.Add(pet);
                StartCoroutine(GoLater(pet, goal, walkers.Count * .15f));
            }
            ToySounds.Play(ToySounds.Cue.Whoosh, Head != null ? Head.position + Head.forward * .5f : transform.position, .4f, 1.3f);
            float until = Time.time + 5f;
            yield return new WaitForSeconds(.4f);
            while (Time.time < until && walkers.Any(w => w.State == RoomCapsule.CapsulePetController.MotionState.Walking)) yield return null;
            // Everyone says why, then the favourite gets a little bump.
            foreach (var (crew, voted, _, say) in votes)
            {
                Body(crew)?.Play(VotedFor.TryGetValue(crew, out _) ? "HandRaise" : "React_Nod");
                SayAbout(crew, say, voted); // F76: the picture it voted for grows while it says why
            }
            var favourite = taken.OrderByDescending(t => t.Value).Select(t => t.Key).FirstOrDefault();
            if (favourite != null)
            {
                StartCoroutine(Bump(favourite));
                ToyFx.Ring(favourite.position + Vector3.down * .02f, Quaternion.Euler(90f, 0f, 0f), .06f);
                Note("decision", "Crew vote", string.Join(", ", votes.Select(v => MakerRoles.Name(v.crew) + ": " + MakerRoles.Clean(v.label))));
            }
            until = Time.time + 16f;
            yield return new WaitForSeconds(1f);
            while (Time.time < until && (lines.Count > 0 || Voice.IsSpeaking || Voice.Loading)) yield return null;
            yield return new WaitForSeconds(Scripted ? .3f : 1.2f);
            // Back to their seats.
            foreach (var pair in homes)
                if (cast.TryGetValue(pair.Key, out var c) && c != null && walkers.Contains(c.movement)) c.movement.GoTo(ClearSeat(pair.Key, pair.Value), 2f);
            until = Time.time + 5f;
            yield return new WaitForSeconds(.3f);
            while (Time.time < until && walkers.Any(w => w.State == RoomCapsule.CapsulePetController.MotionState.Walking)) yield return null;
            foreach (var w in walkers) for (int i = 0; i < 30 && !w.MeetingSeatLocked; i++) { w.LockMeetingSeat(); if (!w.MeetingSeatLocked) yield return null; }
            Comparing = false;
        }

        private IEnumerator GoLater(RoomCapsule.CapsulePetController pet, Vector3 goal, float delay)
        {
            yield return new WaitForSeconds(delay);
            pet.GoTo(goal, 1.5f);
        }

        private static IEnumerator Bump(Transform target)
        {
            Vector3 start = target.localScale;
            for (float t = 0f; t < 1f && target != null; t += Time.deltaTime / .5f)
            {
                target.localScale = start * (1f + Mathf.Sin(t * Mathf.PI) * .14f);
                yield return null;
            }
            if (target != null) target.localScale = start;
        }

        // ------------------------------------------------------------ pros and cons tabs

        // The look being weighed comes forward (bigger, in front of the others) with its tabs on its sides.
        public Picture Focused { get; private set; }
        public const float CompareCardWidth = .1f, CompareCardGap = .008f; // F52: the side cards of the weighed look
        // F52: how wide a look takes in the row: its frame, plus its side cards while it is weighed.
        private float RowWidthOf(Picture p) => p == Focused && Tabs.Count > 0 ? p.size + 2f * (CompareCardGap + CompareCardWidth) + .06f : p.size + .06f;
        private void FocusLook()
        {
            if (Focused == null) return;
            if (Focused.root == null || !Looks.Contains(Focused)) { Focused = null; return; }
            // The expanded group reserves its full height in KeepRowOverBubble; no forward overlap.
            Focused.root.localScale = Vector3.one * GrowOf(Focused.root); // F76: bigger while talked about
        }

        private void ShowProsCons(Crew crew, Picture picture, string[] pros, string[] cons)
        {
            ClearTabs();
            tabsStep = Current;
            tabsUntil = float.PositiveInfinity;
            if (picture != null)
            {
                // F52 (the user: "the compare view looks cheap; everything listed under the pictures is bad - and no
                // hiding behind panels"): one small card on each side of the picture, the good points on the left and
                // the worries on the right, each a short list; the row opens up around this picture
                // (KeepRowOverBubble) so the cards never cover a neighbour, and nothing hangs under the pictures.
                Focused = picture;
                int shown = 0;
                void Side(string[] items, float sideSign, Color color, Color markColor, string mark)
                {
                    var list = (items ?? new string[0]).Select(MakerRoles.Clean).Where(i => !string.IsNullOrWhiteSpace(i)).Take(3).ToList();
                    if (list.Count == 0) return;
                    float height = Mathf.Max(picture.size * .55f, .022f + list.Count * .03f);
                    var tab = PaperTag("Tab " + mark, CompareCardWidth, height, color, out var text, 11f, false); // F77: paper cards, not pills
                    string hex = ColorUtility.ToHtmlStringRGB(markColor);
                    text.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
                    text.lineSpacing = 12f;
                    text.text = string.Join("\n", list.Select(i => "<color=#" + hex + "><b>" + mark + "</b></color>  " + i));
                    tab.SetParent(picture.root, false);
                    tab.localPosition = new Vector3(sideSign * (picture.size * .5f + CompareCardGap + CompareCardWidth * .5f), 0f, -.004f);
                    tab.localRotation = Quaternion.identity;
                    tab.localScale = Vector3.one * .2f;
                    Tabs.Add(tab);
                    StartCoroutine(PopTab(tab, shown++ * .1f));
                }
                Side(pros, -1f, new Color(.93f, .98f, .93f), new Color(.2f, .62f, .32f), "+");
                Side(cons, 1f, new Color(1f, .95f, .93f), new Color(.86f, .36f, .28f), "–");
                ToyFx.Puff(picture.root.position, .012f, 4);
                ToySounds.Play(ToySounds.Cue.Paper, picture.root.position, .45f, 1.4f);
                return;
            }
            Transform onto = picture?.root ?? (Product != null ? Product : Kept);
            Vector3 center = onto != null ? onto.position + (picture != null ? Vector3.zero : Vector3.up * .09f) : AboveCharacter(Crew.Maker, .3f);
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - center, Vector3.up).normalized : Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, toUser);
            float reach = picture != null ? picture.size * .5f + .055f : .09f;
            // F77 (the user: "+ sturdy / - heavy looked like buttons"): beside the made thing too, one paper card per side
            // with its short list (they were separate pills the size of the answer buttons).
            int order = 0;
            void Column(string[] items, Vector3 at, Color color, Color markColor, string mark)
            {
                var list = (items ?? new string[0]).Select(MakerRoles.Clean).Where(i => !string.IsNullOrWhiteSpace(i)).Take(3).ToList();
                if (list.Count == 0) return;
                float height = .022f + list.Count * .03f;
                var tab = PaperTag("Tab " + mark, CompareCardWidth, height, color, out var text, 11f, false);
                string hex = ColorUtility.ToHtmlStringRGB(markColor);
                text.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
                text.lineSpacing = 12f;
                text.text = string.Join("\n", list.Select(i => "<color=#" + hex + ">" + mark + "</color>  " + i));
                tab.position = center;
                tab.rotation = SpeechBubbleDock.FacingAt(at);
                tab.localScale = Vector3.one * .2f;
                Tabs.Add(tab);
                StartCoroutine(SlideTab(tab, at - toUser * .01f, order++ * .1f));
            }
            Column(pros, center - side * (reach + CompareCardWidth * .5f), new Color(.93f, .98f, .93f), new Color(.2f, .62f, .32f), "+");
            Column(cons, center + side * (reach + CompareCardWidth * .5f), new Color(1f, .95f, .93f), new Color(.86f, .36f, .28f), "–");
            ToyFx.Puff(center, .012f, 4);
            ToySounds.Play(ToySounds.Cue.Paper, center, .45f, 1.4f);
        }

        private IEnumerator PopTab(Transform tab, float delay)
        {
            yield return new WaitForSeconds(delay);
            for (float t = 0f; t < 1f && tab != null; t += Time.deltaTime / .25f)
            {
                tab.localScale = Vector3.one * Mathf.Lerp(.2f, 1f, Mathf.SmoothStep(0f, 1f, t));
                yield return null;
            }
            if (tab != null) tab.localScale = Vector3.one;
        }

        private IEnumerator SlideTab(Transform tab, Vector3 home, float delay)
        {
            yield return new WaitForSeconds(delay);
            Vector3 from = tab != null ? tab.position : home;
            for (float t = 0f; t < 1f && tab != null; t += Time.deltaTime / .3f)
            {
                tab.position = Vector3.Lerp(from, home, Mathf.SmoothStep(0f, 1f, t));
                tab.localScale = Vector3.one * Mathf.Lerp(.2f, 1f, Mathf.SmoothStep(0f, 1f, t));
                yield return null;
            }
            if (tab != null) { tab.position = home; tab.localScale = Vector3.one; }
        }

        private void ClearTabs()
        {
            foreach (var t in Tabs) if (t != null) Destroy(t.gameObject);
            Tabs.Clear();
            if (Focused?.root != null) Focused.root.localScale = Vector3.one;
            Focused = null;
        }

        // ------------------------------------------------------------ every frame

        private void CrewFrame()
        {
            DirectorFrame(); // F48
            foreach (var t in Tabs) if (t != null && Focused == null) t.rotation = SpeechBubbleDock.FacingAt(t.position);
            if (Tabs.Count > 0 && Current != tabsStep) ClearTabs();
            if (Current != nudgeStep) { nudgeStep = Current; nudgesInStep = 0; lastStepAt = Time.time; }
            // A teammate takes the initiative when the person has been quiet at a step that waits for them.
            // F30: with nothing chosen yet, the quiet person gets the idea meeting instead (StuckFrame).
            bool waiting = Current == Step.Style || (Current == Step.Looks && LooksShown && Look == null) || Current == Step.Shape || Current == Step.Done;
            float quiet = Time.time - Mathf.Max(lastHeardAt, Mathf.Max(lastLineAt, lastStepAt));
            StuckFrame(quiet);
            if (waiting && Review == ReviewStage.None && Question == null && CurrentLesson == null && !crewBusy && !Comparing && !Sketching && !NotebookOpen && !Holding && !awaitingFit && lines.Count == 0 &&
                !Voice.IsSpeaking && !Voice.Loading && quiet > IdleNudgeAfter && nudgesInStep < 1 && Time.time > nextNudgeAt)
            {
                nudgesInStep++;
                Nudges++;
                nextNudgeAt = Time.time + Mathf.Max(30f, IdleNudgeAfter);
                StartCoroutine(CrewTurn("idle", ""));
            }
        }

        // ------------------------------------------------------------ welcome back and clap

        // The person is back (something was made before): no introductions; the crew greets them.
        public void Welcome()
        {
            if (Begun || arrival == null) return;
            Begun = true;
            beganAt = Time.time;
            Instance = this;
            EnsureCrew();
            arrival.RetireIntroTags();
            foreach (var pair in cast)
            {
                if (!tags.ContainsKey(pair.Key)) tags[pair.Key] = NameTag(pair.Key);
                tags[pair.Key].gameObject.SetActive(true);
            }
            Need = "";
            Current = Step.Suggesting;
            Heard();
            NewProject();
            Restore(); // the last make comes back where it lives (and Spark asks how it went, if not answered)
            bool wallUp = PutUpWall(); // the ideas from before, with a pad of fresh notes
            string greeting = ForceGreeting ?? (awaitingFit ? "fit" : UnityEngine.Random.value < .5f ? "today" : "found");
            if (greeting == "fit" && !awaitingFit) greeting = "today";
            Greeting = greeting;
            Talk.Add("The person is back.");
            if (greeting == "today")
            {
                string[] lines = { "Welcome back! What shall we make today?", "Hi again! What are we making today?", "Back at the bench! What's next?" };
                Say(Crew.Maker, lines[UnityEngine.Random.Range(0, lines.Length)]);
                Body(Crew.Maker)?.Play("Wave");
                if (wallUp) Say(Crew.Maker, "Your ideas are up there. Peel a fresh note for a new one!");
            }
            else if (greeting == "found")
            {
                // F30: no ideas pitched unasked; if the person stays quiet, the meeting comes (StuckFrame).
                AskWith(Crew.Spark, "Ooh, welcome back! What are we making today?", false, IdeaReplies);
                Body(Crew.Spark)?.Play("Cheer");
                if (wallUp) Say(Crew.Maker, "Your ideas are up there. Peel a fresh note for a new one!");
            }
            else Body(Crew.Spark)?.Play("Wave");
            // (F45: no Glance: Spark does not look around the room unasked.)
            Debug.Log("[Windups Maker] Welcome back: " + greeting);
        }

        // Called when the person claps during the session: the crew comes over (the arrival moves them).
        public bool CanCallOver => Begun && !Comparing && !SparkWalking && !Holding && Current != Step.Measuring && !NotebookOpen &&
                                   !PaletteOnPalm && !Drawing && !SizingWithHands && Dragging == null && !Capturing && !tapeDemo && !freeTape;

        public void CrewCalled()
        {
            CallOvers++;
            Heard();
            string[] lines = { "Coming!", "On our way!", "Here we come!" };
            Ack(Crew.Spark, lines[UnityEngine.Random.Range(0, lines.Length)]);
            foreach (var crew in cast.Keys.ToList()) Body(crew)?.PlaySoon("Touch_Jump", .3f);
        }

        // Has this person made something here before (memory or notebook)?
        public static bool HasHistory
        {
            get
            {
                try { return LoadMemory().items.Count > 0 || System.IO.File.Exists(NotebookPath) || System.IO.File.Exists(WallPath); }
                catch { return false; }
            }
        }
    }
}
