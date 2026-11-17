using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Windups.Handoff
{
    // F48 (2026-11-12): live speech goes through the Crew Director (Windups/CrewDirector.cs) when
    // WindupsArrival.crewDirector is on.
    //  - QuickVoice first, with no wait: only words the app is waiting for right now, said on their own (a tap
    //    answer read out, yes / no to Maker's read-back, "done", "the second one", "85 mm" while measuring).
    //  - Everything else is read once by the Director. While it reads, the teammate most likely to answer
    //    thinks (a gesture, no words). Its answer is checked here before anything happens: a tool must be
    //    possible in this step right now, and a tool that changes something runs only for an instruction, an idea
    //    or an answer (a question, a "thanks" or noise never changes the object).
    //  - Tools are the session's existing methods under a name. One that speaks for itself (most do) silences the
    //    Director's own lines, so nobody says a thing twice.
    //  - One reading at a time: words said while one is on its way cancel it and are read together with it.
    //  - No silent fallback: when the reading fails or takes longer than DirectorTimeout, the teammate says so.
    //    The scripted Editor preview and the checks keep the keyword router (Route), unless a check gives
    //    DirectorStub recorded answers.
    public sealed partial class MakerSession
    {
        public static Func<string, string> DirectorStub;   // checks: the request's input json -> a recorded reply (null = the keyword router)
        public static float DirectorTimeout = 8f;          // seconds
        public int DirectorCalls { get; private set; }
        public DirectorReply LastDirectorReply { get; private set; }
        public float LastDirectorSeconds { get; private set; }
        public readonly List<string> DirectorLog = new List<string>(); // what each reading did: "plant_flag", "print REFUSED: not possible now", "say Maker: ..."
        public bool DirectorBusy => directorSeq != directorDone;

        private int directorSeq, directorDone;
        private readonly List<string> heardWaiting = new List<string>();
        private UnityWebRequest directorRequest;
        private bool directorWarm;

        private bool UseDirector => arrival != null && arrival.crewDirector && (!Scripted || DirectorStub != null) && CrewDirector.Ready && CrewDirector.HasManual(Current.ToString());

        // Once per session, before the first words: the contract is read here and parsed off the main thread.
        private void DirectorFrame()
        {
            if (directorWarm || !Begun || arrival == null || !arrival.crewDirector) return;
            directorWarm = true;
            if (CrewDirector.Ready) return;
            var asset = Resources.Load<TextAsset>("CrewDirector");
            if (asset == null) { Debug.LogWarning("[Windups Director] Resources/CrewDirector.json is missing: the keyword router stays on."); return; }
            string json = asset.text;
            Task.Run(() => { try { CrewDirector.Parse(json); } catch (Exception exception) { Debug.LogWarning("[Windups Director] contract: " + exception.Message); } });
        }

        private static readonly HashSet<string> QuickYes = new HashSet<string>
        {
            "yes", "yeah", "yep", "yup", "ok", "okay", "sure", "right", "correct", "do it", "go ahead", "go", "sounds good", "looks good", "perfect", "yes please",
            "yes do it", "make it", "build it", "응", "어", "네", "예", "그래", "좋아", "맞아", "오케이", "해줘", "만들어", "만들어줘"
        };
        private static readonly HashSet<string> QuickNo = new HashSet<string>
        {
            "no", "nope", "nah", "not yet", "wait", "cancel", "hold on", "stop", "no thanks", "아니", "아니야", "아니요", "잠깐", "잠깐만", "아직", "취소"
        };
        private static readonly HashSet<string> QuickDone = new HashSet<string>
        {
            "done", "i'm done", "im done", "that's it", "thats it", "that's all", "thats all", "finished", "next", "다 했어", "다 했어요", "됐어", "끝", "완료", "다음"
        };
        private static readonly string[][] Ordinals =
        {
            new[] { "first", "the first", "the first one", "first one", "left", "the left one", "number one", "one", "첫 번째", "첫번째", "첫 번째 거", "왼쪽", "왼쪽 거" },
            new[] { "second", "the second", "the second one", "second one", "middle", "the middle one", "number two", "two", "두 번째", "두번째", "두 번째 거", "가운데", "가운데 거" },
            new[] { "third", "the third", "the third one", "third one", "right", "the right one", "last", "the last one", "number three", "three", "세 번째", "세번째", "세 번째 거", "오른쪽", "오른쪽 거" }
        };

        private static string Bare(string p) => Regex.Replace(p ?? "", @"[.,!?~]+", "").Trim();

        // Live speech with the Director on.
        private bool DirectorVoice(string text, string p)
        {
            var open = Question != null && !Question.answered && !answeringByReply ? Question : null;
            if (QuickVoice(text, p, open)) return true;
            VoiceAnswersQuestion(); // (F44: said out loud, the open question's card goes; the Director is told what it was)
            DirectorHeard(text, open);
            voiceRoute = DeferredLog;
            return true;
        }

        // Words the app is waiting for, said on their own: done at once, exactly as their button would.
        private bool QuickVoice(string text, string p, OpenQuestion open)
        {
            bool Took(string route) { voiceRoute = "quick: " + route; return true; }
            string q = Bare(p);
            if (q.Length == 0) return false;
            // A tap answer, read out word for word.
            if (open != null)
                for (int i = 0; i < open.chips.Count; i++)
                    if (Bare(open.chips[i].reply.label.ToLowerInvariant()) == q || Bare((open.chips[i].reply.say ?? "").ToLowerInvariant()) == q) { PressReply(i); return Took("tap answer"); }
            // Yes / no to Maker's read-back of v2.
            if (Draft != null && Current == Step.Done && Confirming && open == null)
            {
                if (QuickYes.Contains(q)) { VoiceAnswersQuestion(); BuildFromDraft(); return Took("v2 yes"); }
                if (QuickNo.Contains(q)) { VoiceAnswersQuestion(); EndConfirm(); Ack(Crew.Maker, "Okay, take your time."); return Took("v2 not yet"); }
            }
            if (awaitingTidy && (QuickYes.Contains(q) || QuickNo.Contains(q))) { TidyVoice(QuickYes.Contains(q) ? "yes" : "no"); return Took("tidy"); }
            if (Drawing)
            {
                if (QuickDone.Contains(q)) { FinishDrawing(); return Took("drawing done"); }
                if (q == "undo" || q == "undo that" || q == "되돌려" || q == "취소") { UndoStroke(); return Took("undo line"); }
                return false;
            }
            // A size with its unit while measuring ("85 mm", "about 8.5 cm").
            if (Current == Step.Measuring && Kind != "tray" &&
                Regex.IsMatch(p, @"^(it'?s |it is |about |around |roughly |약 )*\d+([.,]\d+)?\s*(mm|millimet(er|re)s?|밀리(미터)?|cm|centimet(er|re)s?|센티(미터)?)( 정도)?$") && TryMillimetres(p, out float mm))
            { VoiceAnswersQuestion(); SetMeasure(mm, true); ConfirmMeasure(); return Took("size"); }
            if (open != null) return false; // anything else said to an open question is read by the Director
            if (Draft != null && Current == Step.Done && !Confirming && QuickDone.Contains(q)) { StartConfirmOrAsk(); return Took("v2 done"); }
            if (InInputs && QuickDone.Contains(q)) { InputVoice("done", "done"); return Took("inputs done"); }
            if (Current == Step.Finish && QuickDone.Contains(q)) { FinishVoice("done", "done", true); return Took("finish done"); } // F49
            int nth = Array.FindIndex(Ordinals, words => words.Contains(q));
            if (nth >= 0 && Current == Step.Looks && LooksShown && Look == null)
            {
                var looks = ShownLooks();
                if (nth < looks.Count) { PickLook(looks[nth]); return Took("look " + (nth + 1)); }
            }
            if (nth >= 0 && Current == Step.Suggesting && nth < Ideas.Count && Ideas[nth].root != null) { PickIdea(Ideas[nth]); return Took("idea " + (nth + 1)); }
            if (q == "undo" || q == "undo that") { if (UndoPaint()) Ack(Crew.Spark, "Undone!"); return Took("undo"); }
            return false;
        }

        // "Done" / "make v2" with nothing on v2 yet: Maker asks which of the crew's flags goes on it (tap answers).
        private void StartConfirmOrAsk()
        {
            bool anything = Flags.Any(f => f.place == FlagPlace.Planted) || DraftSized;
            var offered = Flags.Where(f => f.place == FlagPlace.Offered && f.who.HasValue).Take(3).ToList();
            if (anything || offered.Count == 0) { StartConfirm(); return; }
            AskWith(Crew.Maker, "Nothing is on v2 yet. Which change goes on it?", true,
                offered.Select(f => new Reply(MakerRoles.Name(f.who.Value) + "'s idea", MakerRoles.Name(f.who.Value) + "'s idea: " + f.text)).ToArray());
        }

        // ------------------------------------------------------------ one reading

        // Words for the Director (spoken, or a tapped answer to a teammate's question).
        private void DirectorHeard(string text, OpenQuestion open)
        {
            heardWaiting.Add(text);
            int seq = ++directorSeq;
            if (directorRequest != null) { try { directorRequest.Abort(); } catch { } } // the reading on its way is dropped: this one carries its words too
            StartCoroutine(DirectorTurn(seq, SpeechState(), open));
        }

        private Crew GuessAnswerer(string heard, OpenQuestion open)
        {
            if (open != null) return open.who;
            string p = heard.ToLowerInvariant();
            if (p.Contains("spark") || p.Contains("스파크")) return Crew.Spark;
            if (p.Contains("fixer") || p.Contains("픽서")) return Crew.Fixer;
            return Crew.Maker;
        }

        // While Maker sketches, carves or paints, its body is busy: no thinking pose or gesture on top of that.
        private bool BodyFree(Crew crew) => !(crew == Crew.Maker && (Sketching || Sculpt != SculptStage.None));

        private IEnumerator DirectorTurn(int seq, string before, OpenQuestion open)
        {
            string heard = string.Join(" ", heardWaiting);
            Crew guess = GuessAnswerer(heard, open);
            bool cue = BodyFree(guess);
            if (cue) Cue(guess, "thinking");
            float started = Time.realtimeSinceStartup;
            DirectorCalls++;
            // What the Director is told, as plain data (taken on the main thread).
            string step = Current.ToString();
            var reviewAt = Review;
            int epochAt = reviewEpoch;
            var flags = DirectorFlags();
            object state = DirectorState(flags);
            string[] recent = Talk.Take(Mathf.Max(0, Talk.Count - 1)).Skip(Mathf.Max(0, Talk.Count - 21)).ToArray();
            object heardData = new
            {
                text = heard,
                openQuestion = open != null ? new { who = MakerRoles.Name(open.who), text = open.text, options = open.chips.Select(c => c.reply.label).ToArray() } : null,
                pointedAt = "", lookingAt = ""
            };
            string json = null, error = null;
            if (DirectorStub != null)
            {
                yield return null;
                json = DirectorStub(JsonConvert.SerializeObject(new { state, recent, heard = heardData }));
                if (json == null)
                {
                    // (a check that records nothing for these words: the keyword router takes them)
                    if (seq != directorSeq) yield break;
                    FinishReading(seq, guess, cue);
                    voiceRoute = null;
                    bool taken = Route(heard, heard.ToLowerInvariant().Trim().TrimEnd('.', '!', '?'));
                    LogSpeech(heard, before, "director stub: none -> " + (voiceRoute ?? (taken ? "step" : "not taken")));
                    yield break;
                }
            }
            else if (!WindupsDirectCredentials.TryGetKey(out string key)) error = "AI access is not configured in this test build.";
            else
            {
                var build = Task.Run(() => CrewDirector.CreateRequest(step, flags, state, recent, heardData));
                while (!build.IsCompleted) yield return null;
                if (this == null) yield break;
                if (seq != directorSeq) yield break;
                if (build.IsFaulted) error = "request: " + build.Exception?.GetBaseException().Message;
                else
                    using (var http = new UnityWebRequest(WindupsDirectMeeting.Endpoint, "POST"))
                    {
                        http.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(build.Result));
                        http.downloadHandler = new DownloadHandlerBuffer();
                        http.SetRequestHeader("Content-Type", "application/json");
                        http.SetRequestHeader("Authorization", "Bearer " + key);
                        http.timeout = Mathf.Max(2, Mathf.CeilToInt(DirectorTimeout));
                        directorRequest = http;
                        yield return http.SendWebRequest();
                        if (directorRequest == http) directorRequest = null;
                        if (this == null) yield break;
                        if (seq != directorSeq) yield break; // newer words took over (they carry these too)
                        if (http.result != UnityWebRequest.Result.Success)
                            error = http.responseCode == 429 ? "AI limit reached. Try again soon." : http.responseCode == 0 ? "took too long or no connection" : "AI error " + http.responseCode;
                        else
                            try { json = MakerRoles.ReadOutput(http.downloadHandler.text); }
                            catch (Exception exception) { error = "unreadable answer: " + exception.Message; }
                    }
            }
            if (seq != directorSeq) yield break;
            FinishReading(seq, guess, cue);
            LastDirectorSeconds = Time.realtimeSinceStartup - started;
            DirectorReply reply = null;
            if (error == null)
                try { reply = JsonConvert.DeserializeObject<DirectorReply>(json); }
                catch (Exception exception) { error = "unreadable answer: " + exception.Message; }
            if (reply == null)
            {
                error ??= "empty answer";
                LastError = error;
                Debug.LogWarning("[Windups Director] " + error);
                Caption(guess, error.StartsWith("AI ", StringComparison.Ordinal) ? error : "I lost that. Say it again?", 3.5f);
                LogSpeech(heard, before, "director failed: " + error, new { seconds = LastDirectorSeconds });
                yield break;
            }
            if (step != Current.ToString() || reviewAt != Review || epochAt != reviewEpoch)
            { LogSpeech(heard, before, "director: discarded after the user's decision changed"); yield break; }
            LastDirectorReply = reply;
            Debug.Log("[Windups Director] \"" + heard + "\" -> " + json);
            PerformDirector(reply, heard, before);
        }

        private void FinishReading(int seq, Crew guess, bool cue)
        {
            directorDone = seq;
            heardWaiting.Clear();
            if (cue && cast.TryGetValue(guess, out var c) && c != null && c.Cue == "thinking") c.SetCue("idle");
        }

        // The Director's answer, checked and carried out.
        private void PerformDirector(DirectorReply r, string heard, string before)
        {
            string intent = r.intent ?? "unclear";
            bool mayChange = intent == "instruction" || intent == "idea" || intent == "answer";
            if (!Enum.TryParse(r.to, out Crew to)) to = Crew.Maker;
            if (intent == "answer" && !QuestionLike(heard.ToLowerInvariant()) && !JustReaction(heard.ToLowerInvariant())) CloseQuestion("director accepted answer");
            var did = new List<string>();
            bool narrated = false, failed = false;
            foreach (var a in (r.actions ?? new DirectorAction[0]).Where(a => a != null).Take(3))
            {
                var tool = CrewDirector.Tool(a.tool);
                string why = tool == null ? "unknown tool"
                    : !CrewDirector.IsAvailable(tool, Current.ToString(), DirectorFlags()) ? "not possible now"
                    : tool.changes && !mayChange ? "not asked for (" + intent + ")" : null;
                bool ok = why == null && RunTool(tool, a, heard, out why);
                did.Add(ok ? a.tool : a.tool + " REFUSED: " + why);
                if (ok) { narrated |= tool.narrates; Talk.Add("App: " + ToolSummary(a)); }
                else failed |= mayChange; // (a change refused for a question is no failure: the answer is the words)
            }
            // The crew's own words, unless a tool already spoke for itself.
            bool spoke = narrated;
            if (!narrated)
            {
                foreach (var l in (r.lines ?? new DirectorLine[0]).Where(l => l != null).Take(2))
                {
                    if (!Enum.TryParse(l.who, out Crew who)) who = to;
                    string text = MakerRoles.Clean(l.text);
                    if (!CrewDirector.Showable(text)) { did.Add("dropped a line that is not English"); text = ""; }
                    if (BodyFree(who)) Play(who, l.gesture);
                    if (text.Length == 0) continue;
                    did.Add("say " + who + ": " + text);
                    Say(who, text);
                    spoke = true;
                }
                var ask = r.ask;
                if (ask != null && !string.IsNullOrWhiteSpace(ask.text) && CrewDirector.Showable(ask.text) && (ask.options ?? new string[0]).All(CrewDirector.Showable))
                {
                    if (!Enum.TryParse(ask.who, out Crew asker)) asker = to;
                    did.Add("ask " + asker + ": " + ask.text);
                    AskCrewQuestion(asker, MakerRoles.Clean(ask.text), ask.options);
                    spoke = true;
                }
            }
            if (failed && !spoke) Caption(to, "I can't do that right now.", 2.8f);
            if (mayChange && !string.IsNullOrWhiteSpace(r.note) && CrewDirector.Showable(r.note)) Note("decision", "Noted", MakerRoles.Clean(r.note));
            DirectorLog.AddRange(did);
            if (DirectorLog.Count > 200) DirectorLog.RemoveRange(0, DirectorLog.Count - 200);
            LogSpeech(heard, before, "director: " + intent + " -> " + (did.Count > 0 ? string.Join("; ", did) : "nothing"),
                new { seconds = Math.Round(LastDirectorSeconds, 2), r.lang, intent, r.to, did, r.note });
        }

        private static string ToolSummary(DirectorAction a) =>
            a.tool + (string.IsNullOrWhiteSpace(a.text) ? "" : " \"" + a.text + "\"") + (a.index >= 0 ? " " + a.index : "") +
            (!string.IsNullOrEmpty(a.who) && a.who != "none" ? " " + a.who : "") + (a.mm > 0f ? " " + a.axis + " " + a.mm.ToString("0") + " mm" : "");

        private void Play(Crew crew, string gesture)
        {
            string clip = gesture == "nod" ? "React_Nod" : gesture == "shake" ? "React_Shake" : gesture == "idea" ? "Idea_Pop" : gesture == "cheer" ? "Cheer"
                : gesture == "present" ? "Decision_Present" : gesture == "wave" ? "Wave" : gesture == "tilt" ? "HeadTilt" : null;
            if (clip != null) Body(crew)?.Play(clip);
        }

        // ------------------------------------------------------------ what the Director is told

        private Dictionary<string, bool> DirectorFlags()
        {
            bool draft = Draft != null && Current == Step.Done;
            bool planted = draft && Flags.Any(f => f.place == FlagPlace.Planted);
            return new Dictionary<string, bool>
            {
                { "draft", draft }, { "confirming", draft && Confirming }, { "planted", planted }, { "v2Changed", planted || (draft && DraftSized) },
                { "offered", draft && Flags.Any(f => f.place == FlagPlace.Offered && f.who.HasValue) },
                { "looks", Current == Step.Looks && LooksShown && Look == null && ShownLooks().Count > 0 },
                { "ideas", Current == Step.Suggesting && Ideas.Any(c => c.root != null) },
                { "drawing", Drawing }, { "notebookOpen", NotebookOpen }, { "showCircle", ShowCircleShown },
                { "made", Product != null && Current == Step.Done }, { "awaitingFit", awaitingFit }, { "freeMeasure", AwaitingMeasureUse },
                { "hasGuess", Current == Step.Measuring && Hint > 0f }, { "palette", Palette.Count > 0 }, { "canPin", ShownLooks().Count > 0 || Look != null }
            };
        }

        private object FlagData(Flag f) => new { by = f.who.HasValue ? MakerRoles.Name(f.who.Value) : "person", text = f.text, part = f.part };

        private object DirectorState(Dictionary<string, bool> flags)
        {
            string activity = Current == Step.Planning ? "working the design out"
                : Sculpt == SculptStage.Paint ? "painting it with the big brush"
                : Sculpt == SculptStage.Reveal ? "showing the finished thing"
                : Sculpt == SculptStage.Shape ? "shaping it out of the wood block"
                : Sculpt == SculptStage.Approach || Sculpt == SculptStage.Clay ? "carving the wood block"
                : Current == Step.Making ? "building it"
                : Sketching ? "sketching the pictures in its book" : "";
            object draft = null;
            if (flags["draft"])
            {
                Vector3 now = DraftSizeMm, first = V1Size();
                draft = new
                {
                    planted = Flags.Where(f => f.place == FlagPlace.Planted).Select(FlagData).ToArray(),
                    offered = Flags.Where(f => f.place == FlagPlace.Offered).Select(FlagData).ToArray(),
                    setAside = Flags.Where(f => f.place == FlagPlace.Parked).Select(FlagData).ToArray(),
                    sizeMm = new { width = Mathf.Round(now.x), height = Mathf.Round(now.y), depth = Mathf.Round(now.z) },
                    firstOneMm = new { width = Mathf.Round(first.x), height = Mathf.Round(first.y), depth = Mathf.Round(first.z) }
                };
            }
            return new
            {
                step = Current.ToString(),
                review = Review.ToString(),
                shapeConfirmed = ShapeConfirmed,
                shapeRequirements = ShapeRequirements,
                personRules = ShapeRules.ToArray(), // F63
                workflow = ShapeWorkflow, // F77
                need = Need,
                forObject = TargetName ?? Target?.seen.name ?? ForObjectName ?? "",
                activity,
                flags,
                looks = ShownLooks().Select((l, i) => new { index = i, label = MakerRoles.Clean(l.look?.label ?? ""), prompt = l.look?.prompt ?? "" }).ToArray(),
                lookRound = Current == Step.Looks ? LookJob : "",            // F49: forms / finishes / looks
                vibe = VibeWords,
                vibeOffered = Current == Step.Finish && VibeOffered,
                // F49: the lesson step a teammate is teaching right now (the tape, the palette, a colour, a texture, paint, erase).
                lesson = CurrentLesson != null ? new { step = CurrentLesson.name, by = MakerRoles.Name(CurrentLesson.who), said = MakerRoles.Clean(CurrentLesson.line) } : null,
                chosenLook = Look?.label ?? "",
                ideas = Ideas.Where(c => c.root != null).Select((c, i) => new { index = i, label = MakerRoles.Clean(c.idea.label), by = MakerRoles.Name(c.who) }).ToArray(),
                measuredMm = HasMeasure ? Mathf.Round(Measured) : 0f,
                sizeGuessMm = Mathf.Round(Hint),
                shape = string.IsNullOrEmpty(Shape) ? ShapeWords : Shape,
                colours = string.IsNullOrEmpty(ColourWords) ? Style : ColourWords,
                made = Product != null && Current == Step.Done && Design != null ? Design.name + ": " + Design.Summary : "",
                draft,
                palette = Palette.Skip(Mathf.Max(0, Palette.Count - 6)).Select(s => (s.kind == "texture" ? "texture of " : "") + s.name + " " + s.hex).ToArray(),
                decisions = Notes.Where(n => n.project == project).Select(n => n.title + ": " + n.text).TakeLast(8).ToArray()
            };
        }

        // ------------------------------------------------------------ the tools: the session's own methods, by name

        private bool RunTool(DirectorTool tool, DirectorAction a, string heard, out string why)
        {
            why = null;
            string text = MakerRoles.Clean(a.text).Trim();
            string p = text.ToLowerInvariant().TrimEnd('.', '!', '?');
            bool Need(bool ok, string reason, out string w) { w = ok ? null : reason; return ok; }
            Crew who = Crew.Maker;
            bool hasWho = !string.IsNullOrEmpty(a.who) && Enum.TryParse(a.who, out who);
            if (FormFirst && !ShapeConfirmed && tool.name.StartsWith("finish_")) { why = "the person must tap Start colouring first"; return false; }
            if (FormFirst && Review != ReviewStage.None && tool.name == "change_made")
            { if (Review == ReviewStage.Shape) { if (Draft == null) EnterChangeMode(); PersonFlag(text); return true; } why = "review is waiting for a decision"; return false; }
            if (FormFirst && (Review == ReviewStage.RevisionPicture || Review == ReviewStage.DrawingRevision || Review == ReviewStage.Placement) && tool.changes)
            { why = "use the current review choices"; return false; }
            switch (tool.name)
            {
                // ---- any time
                case "crew_opinions": StartCoroutine(CrewTurn("said", text.Length > 0 ? text : heard)); return true;
                case "open_notebook": OpenNotebook(); return true;
                case "close_notebook": CloseNotebook(); return true;
                case "next_page": FlipNotebook(1); return true;
                case "previous_page": FlipNotebook(-1); return true;
                case "show_tape": StartCoroutine(TeachTape()); return true;
                case "look_around": LookAroundNow(); return true;
                case "name_things": StartCoroutine(NameThings()); return true;
                case "fetch_colour": if (!Need(text.Length > 0, "no thing named", out why)) return false; StartCoroutine(FetchSwatch(text)); return true;
                case "use_palette": return Need(ToolsVoice("use my palette", "use my palette"), "the palette is empty", out why);
                case "draw_myself": StartCoroutine(HandSketchbook()); return true;
                case "finish_drawing": FinishDrawing(); return true;
                case "undo_line": UndoStroke(); return true;
                case "clear_drawing": ClearPageKeepUndo(); Ack(Crew.Maker, "Fresh page!"); return true;
                case "undo_paint": if (!Need(UndoPaint(), "nothing to undo", out why)) return false; Ack(Crew.Spark, "Undone!"); return true;
                case "pin_looks": return Need(Make3DVoice("pin them on the wall"), "nothing to pin", out why);
                case "start_over":
                    // Never at a word: one tap confirms it. F77: the same New creation / Keep going question as the router's.
                    AskNewCreation();
                    return true;
                case "fit_feedback": return Need(FitAnswer(p.Contains("tight") ? "too tight" : p.Contains("loose") ? "too loose" : "perfect"), "no fit was asked", out why);
                case "use_measure": return Need(UseFreeMeasure(text), "no current measurement or purpose", out why);
                // ---- nothing chosen yet
                case "find_ideas": FindIdeas("said"); return true;
                case "pick_idea":
                {
                    var notes = Ideas.Where(c => c.root != null).ToList();
                    if (!Need(a.index >= 0 && a.index < notes.Count, "no such idea note", out why)) return false;
                    PickIdea(notes[a.index]);
                    return true;
                }
                case "set_need": if (!Need(text.Length > 0, "nothing to make was named", out why)) return false; return StepInput(text, p);
                // ---- the thing it is for, the shape, the colours
                case "skip_object": SkipForObject("said no"); return true;
                case "take_picture": TakeShowPicture(true); return true;
                case "add_shape":
                case "add_colour":
                    if (!Need(text.Length > 0, "nothing was described", out why)) return false;
                    AddInputWords(tool.name == "add_shape", text);
                    return true;
                case "match_thing": if (!Need(Eyes != null && !Eyes.Looking, "Spark is already looking", out why)) return false; StartCoroutine(LookForInput(text.Length > 0 ? text : heard)); return true;
                case "crew_picks": return InputVoice("you pick", "you pick");
                case "inputs_done": return InputVoice("done", "done");
                // ---- the pictures
                case "pick_look":
                {
                    var looks = ShownLooks();
                    if (!Need(a.index >= 0 && a.index < looks.Count, "no such picture", out why)) return false;
                    PickLook(looks[a.index]);
                    return true;
                }
                case "redraw_looks": if (!Need(text.Length > 0, "no change was named", out why)) return false; return StepInput(text, p);
                // ---- measuring
                case "set_size": if (!Need(a.mm >= 10f && a.mm <= 2000f, "not a size", out why)) return false; SetMeasure(a.mm, true); ConfirmMeasure(); return true;
                case "use_guess": return StepInput("use your guess", "use your guess");
                case "measure_again": return StepInput("again", "again");
                // ---- while it is built, and after
                case "queue_change":
                    if (!Need(text.Length > 0, "no change was named", out why)) return false;
                    pendingChange = text;
                    Ack(Crew.Maker, "Got it, right after this!");
                    return true;
                case "change_made":
                    if (!Need(text.Length > 0, "no change was named", out why)) return false;
                    if (WantsVersion(p)) nextAsVersion = true;
                    Shape = (Shape + ", " + text).Trim(' ', ',');
                    StartCoroutine(Plan(text));
                    return true;
                // ---- F49: the finish of the white clay shape
                case "finish_vibe": if (!Need(text.Length > 0, "no vibe was named", out why)) return false; StartVibe(text); return true;
                case "finish_done": return FinishVoice("done", "done", true);
                case "finish_skip": return FinishVoice("skip", "skip", true);
                case "print": Print(); return true;
                case "print_check": SayPrintCheck(false); return true;
                // ---- v2
                case "plant_flag":
                    if (!Need(text.Length > 0, "no change was named", out why)) return false;
                    PersonFlag(text, a.part == "none" ? null : a.part);
                    return true;
                case "plant_crew_flag":
                {
                    var f = hasWho ? Flags.FirstOrDefault(x => x.who == who && x.place == FlagPlace.Offered) ?? Flags.FirstOrDefault(x => x.who == who && x.place == FlagPlace.Parked) : null;
                    if (!Need(f != null && f.root != null, "that teammate has no flag to plant", out why)) return false;
                    if (!PartPointOnDraft(f.part, (int)who, out Vector3 local, out Vector3 normal)) { local = draftSource.bounds.center + Vector3.up * draftSource.bounds.extents.y; normal = Vector3.up; }
                    f.root.position = draftMesh.TransformPoint(local);
                    Plant(f, local, normal);
                    Body(who)?.Play("React_Nod");
                    return true;
                }
                case "remove_flag":
                {
                    var f = hasWho ? Flags.FirstOrDefault(x => x.who == who && x.place == FlagPlace.Planted) : Flags.LastOrDefault(x => !x.who.HasValue && x.place == FlagPlace.Planted);
                    if (!Need(f != null, "no such flag on v2", out why)) return false;
                    if (f.who.HasValue) GiveBack(f); else RemoveFlag(f, true);
                    return true;
                }
                case "resize_v2":
                {
                    string words = a.mm > 0f
                        ? (a.axis == "height" ? "height " : a.axis == "width" ? "width " : a.axis == "depth" ? "depth " : "") + a.mm.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " mm"
                        : new[] { "bigger", "smaller", "taller", "shorter", "wider", "narrower", "deeper" }.FirstOrDefault(p.Contains);
                    return Need(words != null && DraftSizeWords(words), "no size to change to", out why);
                }
                case "measure_v2": MeasureDraft(); return true;
                case "start_confirm": StartConfirmOrAsk(); return true;
                case "confirm_build": if (!Need(Confirming, "nothing was read back yet", out why)) return false; BuildFromDraft(); return true;
                case "cancel_confirm": EndConfirm(); Ack(Crew.Maker, "Okay, take your time."); return true;
                default:
                    why = "no such tool in the app";
                    return false;
            }
        }

        // A described shape or colour, kept as it was said (the Director already decided it is one).
        private void AddInputWords(bool shape, string text)
        {
            if (shape) { AddWordsCard(text); return; } // F56: the shape is collected as cards
            ColourWords = (ColourWords + ", " + text).Trim(' ', ',');
            Ack(Crew.Spark, "Ooh, " + text + ".");
            GotInput();
        }
    }
}
