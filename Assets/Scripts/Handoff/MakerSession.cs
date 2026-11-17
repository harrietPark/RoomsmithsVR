using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace Windups.Handoff
{
    // F26 "Make what your space needs" (first object: a coaster for the user's own mug), with the
    // maker crew Spark (ideas, looks, memory), Maker (lead: sketch, build, print) and Fixer (fit).
    // After the clap, the introductions and the sticky note ("What do you want to make today?"):
    //  0. Not sure? "Look around": Spark walks over, looks with the camera and suggests what to make.
    //  1. Maker asks the vibe. "Like this mug" (a real object or a picture on a monitor/phone):
    //     Spark looks and takes its colours/texture. Maker sketches three looks; pinch one.
    //  2. The person measures with the hand tape (Spark's camera guess is only a hint).
    //  3. Maker asks the shape; Fixer checks the plan (gap, thickness) and fixes it first.
    //  4. Maker builds it right away ON the real mug (layer by layer; no see-through preview and no
    //     "make it" since 2026-11-11). The app builds the shape itself (F30: Meshy for other objects).
    //     Fixer says how it fits, on the made thing. A spoken change rebuilds it in place (its paint
    //     stays); a flag or "new version" keeps the last one beside it. Grab it, move it, let go: it
    //     lands on the real desk (MRUK colliders). Spark remembers where it lives for next time.
    //  5. "Print it": an STL in millimetres is saved on the headset and sent to the PC.
    // F29 (Thinkmade workbench) adds a crew layer on top (MakerSession.Crew.cs): questions and requests
    // that are not plain step input go to the whole crew, who pick actions (stickers on the looks, a
    // vote by standing under a look, pros and cons, a question back, a look around) and take the
    // initiative when the person is quiet; Maker keeps a notebook of every decision
    // (MakerSession.Notebook.cs); a returning person is welcomed without the introductions.
    public sealed partial class MakerSession : MonoBehaviour
    {
        public static MakerSession Instance { get; private set; }
        // Planning: Maker works out the design (AI, Meshy); Making: Fixer's fixes and the build; Done: it is here.
        // F33: ShapeInput and ColourInput (the person gives the shape, then the colours, each in the way they
        // like) replace the old one-question Style step (kept in the enum, no longer entered).
        // F45: ForObject (the thing it is for goes in the circle) comes between what to make and ShapeInput.
        public enum Step { Suggesting, Style, Looks, Measuring, Shape, Planning, Making, Done, ShapeInput, ColourInput, ForObject, Finish }
        // F79 (Quest 2026-11-14: "the bubbles are far too high - right above the head"): the dock's anchor is already
        // 3.5 cm over the head top; with the arrival's 4 cm card lift this leaves the bubble about 3-4 cm over the head
        // (it was 16 cm, keeping room for the name tag, which now hides behind a speaking bubble).
        public const float SpeechLift = -.055f;
        public const float CrewTagHeight = .022f; // F81
        public const string MemoryFile = "maker_memory.json";

        public WindupsArrival arrival;
        public bool Begun { get; private set; }
        // F92: the step is a property so a change can let the next line through (StepChanged, MakerSession.Calm.cs).
        public Step Current { get => currentStep; private set { if (currentStep == value) return; currentStep = value; StepChanged(); } }
        private Step currentStep;
        public string Need { get; private set; } = "";
        public string Style { get; private set; } = "";
        public string Shape { get; private set; } = "";
        public MakerLook Look { get; private set; }
        public float Measured { get; private set; }          // mm, from the person's hand tape (or said)
        public bool HasMeasure { get; private set; }
        public float Hint { get; private set; }              // mm, Spark's camera guess
        public Vector3 MugSpot { get; private set; }         // where the real object stands
        public bool HasSpot { get; private set; }
        public CoasterDesign Design { get; private set; }
        public float Gap { get; private set; }               // mm per side after Fixer's check
        public bool FixedByFixer { get; private set; }
        public string Material { get; private set; } = "";
        public string LastSay { get; private set; } = "";
        public Crew? LastSpeaker { get; private set; }
        public string LastError { get; private set; }
        public string LastPrint { get; private set; }
        public bool LastPrintSent { get; private set; }
        public bool Remembered { get; private set; }
        public float PrintMinutes { get; private set; }
        public StudioVoice Voice { get; private set; }
        public RoomEyes Eyes { get; private set; }
        public byte[] Reference { get; private set; }        // a real object's crop: colours and texture
        public RoomEyes.Found Matched { get; private set; }
        public RoomEyes.Found Target { get; private set; }   // the object we are making something for
        public readonly HandTape Tape = new HandTape();
        public readonly List<string> Talk = new List<string>();
        public readonly List<Transform> Tags = new List<Transform>();
        public bool Working(Crew c) => working.Contains(c);
        public bool SparkWalking { get; private set; }
        // The real object the person means ("this mug"), found by pointing or by looking, with its name tag.
        public string TargetName { get; private set; }
        public bool Spotting { get; private set; }
        public Transform TargetTag => targetTag;
        public int Spotted { get; private set; }
        private Transform targetTag;
        public bool Sketching { get; private set; }          // Maker draws on its own pad while the looks are made
        public int Drafts { get; private set; }              // pages Maker crumpled and threw away
        public bool LooksShown { get; private set; }         // the finished pictures came out of Maker's book
        public const float LooksLift = .38f, ChosenLift = .22f; // picture row / chosen picture height over the crew's feet
        public float SparkReach { get; private set; }        // how far Spark went to look (m)

        public sealed class Picture
        {
            public Transform root;
            public RawImage image;
            public TextMeshProUGUI label;
            public Texture2D texture;
            public byte[] jpeg;
            public MakerLook look;
            public float size;
            public Vector3 home;
            public bool Done;                          // the picture request finished (drawn or failed)
            public bool Ready => texture != null;
        }
        public sealed class Choice
        {
            public Transform root;
            public MakerIdea idea;
            public RoomEyes.Found found;
            public float width, height;
            public Vector3 home;
            public MeetSpot spot;      // F30: the real spot its note is stuck on
            public Crew who;           // F30: the teammate who wrote it
            public bool placed;
        }
        public readonly List<Picture> Looks = new List<Picture>();
        public readonly List<Choice> Ideas = new List<Choice>();
        public Transform Product { get; private set; }
        public Transform Kept { get; private set; }          // last time's make, back where it lives
        public Rigidbody ProductBody { get; private set; }
        public Mesh ProductMesh { get; private set; }
        public bool Holding => holdHand != null || mouseHolding || NotebookHeld;

        private readonly HashSet<Crew> working = new HashSet<Crew>();
        private readonly Dictionary<Crew, WindupCharacter> cast = new Dictionary<Crew, WindupCharacter>();
        private readonly Dictionary<Crew, Transform> tags = new Dictionary<Crew, Transform>();
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly Dictionary<Hand, bool> wasPinching = new Dictionary<Hand, bool>();
        private readonly Dictionary<Hand, float> thumbsSince = new Dictionary<Hand, float>();
        private readonly Queue<(Crew crew, string text)> lines = new Queue<(Crew, string)>();
        private readonly List<Vector3> heldPath = new List<Vector3>();
        private readonly List<float> heldTimes = new List<float>();
        private Material material, ghostMaterial;
        private TMP_FontAsset font;
        private Transform tapeBar, tapeLabelCard, endA, endB, gapCard, magnifier, focus, sketchPad, pencil, wrench;
        private readonly List<Transform> pages = new List<Transform>();
        private readonly List<string> sketchShapes = new List<string>();
        private Mesh pageMesh;
        private Vector2 pencilTip;
        private float pencilLift;
        private bool sketchWanted;
        private int drawHand; // +1 right, -1 left mitten holds the pencil (chosen once per sketch)
        private float drawSide = -1f; // the drawing hand's side of the page (pad x sign)
        private Vector3 penHand;
        private TextMeshProUGUI tapeLabel, gapLabel;
        private Hand holdHand;
        private Vector3 holdOffset;
        private Quaternion holdTurn;
        private Quaternion standCarryTurn = Quaternion.identity; // F97: a carried stand keeps the turn it had when taken
        private Vector3 standCarryOffset; // F98: from the pinch to the stand, in the world (no swing with the wrist)
        private float standGrabY;         // F98: the height it was taken from
        private bool mouseHolding, awaitingFit, looking;
        private Plane mousePlane;
        private float nextLineAt, buildFrom = -1f, captionUntil;
        private WindupCharacter captioned;
        private float fitBias;                               // mm added to the gap after "it was too tight"

        private Transform Head => arrival != null ? arrival.handoff.meeting.Head : null;
        private bool Scripted => arrival != null && arrival.handoff.meeting.LocalDemoEnabled;
        public static string MemoryPath => Path.Combine(Application.persistentDataPath, MemoryFile);

        private void Awake() => Instance = this;
        private void OnDestroy()
        {
            FlushNotebookSnapshot();
            Body(Crew.Spark)?.Hold(true, null);
            if (Instance == this) Instance = null;
            foreach (var o in owned) if (o != null) Destroy(o);
        }

        // ------------------------------------------------------------ start

        public void Begin(string need)
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
            Need = need?.Trim() ?? "";
            Talk.Add("Person said: " + Need);
            Heard();
            if (Need.Length > 0) { Note("brief", "What we're making", Need); RememberWallNote(Need); }
            Restore();
            // F30: ideas only when asked for, or after a quiet while (StuckFrame); an empty note just waits.
            if (Need.Length == 0) { Current = Step.Suggesting; if (!quietStart) AskWith(Crew.Maker, "So, what shall we make today?", false, IdeaReplies); }
            else if (AsksForIdeas(Need.ToLowerInvariant())) { Current = Step.Suggesting; StartCoroutine(Suggest("said")); }
            else if (StuckWords(Need.ToLowerInvariant())) { Current = Step.Suggesting; PointAtIdeasButton(); }
            else AfterNeed(); // F45: "a mug coaster" -> put your mug in the circle first
            StartCoroutine(Research());
            // (F45: no Glance here any more: Spark does not look around the room unasked.)
            Debug.Log("[Windups Maker] Begun: " + Need);
        }

        // Voice, materials, the crew and Spark's eyes; also before the session begins (spotting the object
        // the topic names).
        private void EnsureCrew()
        {
            Instance = this;
            if (Voice == null) Voice = GetComponent<StudioVoice>() ?? gameObject.AddComponent<StudioVoice>();
            var bubble = arrival.guide.authoredBubble;
            if (material == null) material = bubble.GetComponent<MeshRenderer>().sharedMaterial;
            if (ghostMaterial == null) ghostMaterial = Resources.Load<Material>("MakerGhost");
            if (font == null) font = bubble.GetComponent<SpeechBubblePresentation>()?.toyFont;
            foreach (var dock in arrival.handoff.docks)
                if (dock.character != null && MakerRoles.TryForCharacter(dock.character.characterName, out var crew)) cast[crew] = dock.character;
            if (tapeBar == null) BuildTools();
            if (gear == null) BuildGear(); // F55: Fixer's settings gear
            LoadSettings();                // F55: the saved Voice / Sounds settings
            if (Eyes == null)
            {
                Eyes = GetComponent<RoomEyes>() ?? gameObject.AddComponent<RoomEyes>();
                Eyes.arrival = arrival;
            }
            Eyes.Prepare();
        }

        // ------------------------------------------------------------ the object the person means

        // The person names a real object ("a coaster for this mug"): Spark finds it where the finger points
        // (pointing in the last few seconds) or by looking around, and puts its name tag on it.
        public void Spot(string words)
        {
            if (arrival == null || string.IsNullOrWhiteSpace(words) || Spotting) return;
            string p = words.ToLowerInvariant();
            string noun = ObjectNoun(p);
            bool pointed = WindupsGestures.RecentPoint(6f, out _);
            // F45: only where the person points; a thing only named waits for the circle (MakerSession.ForObject.cs).
            if (!pointed || (noun == null && !PointsAtSomething(p))) return;
            EnsureCrew();
            StartCoroutine(SpotTarget(words, noun));
        }

        // Everyday things people make something for (English and Korean words -> the tag's name).
        private static readonly (string word, string name)[] Things =
        {
            ("mug", "mug"), ("cup", "cup"), ("glass", "glass"), ("bottle", "bottle"), ("tumbler", "tumbler"), ("can", "can"),
            ("plant", "plant"), ("pot", "pot"), ("vase", "vase"), ("candle", "candle"), ("phone", "phone"), ("laptop", "laptop"),
            ("keyboard", "keyboard"), ("mouse", "mouse"), ("monitor", "monitor"), ("headphones", "headphones"), ("speaker", "speaker"),
            ("book", "book"), ("pen", "pen"), ("pencil", "pencil"), ("remote", "remote"), ("controller", "controller"), ("watch", "watch"),
            ("keys", "keys"), ("charger", "charger"), ("cable", "cable"), ("tablet", "tablet"), ("bowl", "bowl"), ("box", "box"),
            ("머그", "mug"), ("컵", "cup"), ("잔", "glass"), ("병", "bottle"), ("텀블러", "tumbler"), ("캔", "can"), ("화분", "plant"),
            ("꽃병", "vase"), ("양초", "candle"), ("핸드폰", "phone"), ("휴대폰", "phone"), ("폰", "phone"), ("노트북", "laptop"),
            ("키보드", "keyboard"), ("마우스", "mouse"), ("모니터", "monitor"), ("헤드폰", "headphones"), ("책", "book"), ("펜", "pen"),
            ("리모컨", "remote"), ("시계", "watch"), ("열쇠", "keys"), ("충전기", "charger"), ("케이블", "cable"), ("그릇", "bowl")
        };

        public static string ObjectNoun(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            p = p.ToLowerInvariant();
            // Prefer the thing after "this / my / for / fits ..." ("a coaster for my coffee mug" -> mug).
            string best = null;
            int bestAt = int.MaxValue;
            foreach (var (word, name) in Things)
            {
                var m = Regex.Match(p, word.All(c => c < 128) ? @"\b" + word + @"s?\b" : word);
                if (!m.Success) continue;
                bool pointedAt = Regex.IsMatch(p.Substring(0, m.Index), @"(this|that|my|for|fits?|the|이|저|내|그)\s*(\w+\s+)?$");
                int rank = (pointedAt ? 0 : 1000) + m.Index;
                if (rank < bestAt) { bestAt = rank; best = name; }
            }
            return best;
        }

        private IEnumerator SpotTarget(string words, string noun)
        {
            Spotting = true;
            bool pointed = WindupsGestures.RecentPoint(6f, out Ray ray);
            var room = arrival.handoff.meeting.environment;
            bool hitRoom = Physics.Raycast(ray, out var hit, 4f, room.GeometryMask, QueryTriggerInteraction.Ignore);
            cast.TryGetValue(Crew.Spark, out var spark);
            var sparkBody = WindupBody.Of(spark);
            if (pointed && hitRoom) sparkBody?.LookAtPoint(hit.point, 4f);
            Ack(Crew.Spark, pointed ? "That one?" : "Let me find it!");
            List<RoomEyes.Found> seen = null;
            string question = "the real object the person means" + (noun != null ? " (a " + noun + ")" : "") + " when they say: '" + words + "'";
            yield return SparkLooks(question, list => seen = list);
            RoomEyes.Found pick = null;
            // Only what Spark could place in the room can carry the tag (a thing seen but not placed has no spot).
            seen = seen?.Where(f => f != null && f.placed).ToList();
            if (seen != null && seen.Count > 0)
            {
                // Pointing wins: the object closest to the finger's line.
                if (pointed)
                    pick = seen.Select(f => (f, d: RayDistance(ray, Middle(f)))).Where(x => x.d < .2f).OrderBy(x => x.d).Select(x => x.f).FirstOrDefault();
                if (pick == null && noun != null) pick = seen.FirstOrDefault(f => f.seen.name.ToLowerInvariant().Contains(noun));
                if (pick == null && !pointed) pick = seen.FirstOrDefault(f => f.seen.focus);
            }
            if (pick != null)
            {
                Target = pick;
                TargetName = noun ?? MakerRoles.Clean(pick.seen.name);
                HideSeenLabel(pick);
                ShowTargetTag(pick, TargetName, false);
                sparkBody?.LookAtPoint(pick.top, 3f);
                sparkBody?.Play("Peek");
                Talk.Add("The person means their " + TargetName + " (Spark found it" + (pointed ? " where they pointed" : "") + ")");
                Say(Crew.Spark, "Got it, your " + TargetName + "!");
                Spotted++;
                if (Current == Step.ForObject) LeaveForObject(); // F45: found where the finger pointed, no need for the circle
            }
            else if (pointed && hitRoom)
            {
                // Nothing recognised there: still mark the spot the finger points at.
                TargetName = noun ?? "this";
                ShowTargetTagAt(hit.point, TargetName);
                Say(Crew.Spark, "This one? I'll look closer when we measure.");
                Spotted++;
            }
            else Say(Crew.Spark, Eyes.LastError ?? "I can't spot " + (noun != null ? "the " + noun : "it") + ". Point at it and say it again?");
            Spotting = false;
        }

        private static Vector3 Middle(RoomEyes.Found f) => f.onSurface ? (f.basePoint + f.top) * .5f : f.top;
        private static float RayDistance(Ray ray, Vector3 p)
        {
            Vector3 to = p - ray.origin;
            if (Vector3.Dot(to, ray.direction) <= 0f) return float.PositiveInfinity;
            return Vector3.Cross(ray.direction, to).magnitude;
        }

        // One name tag for the object the person means; it stays (other tags come and go).
        private void ShowTargetTag(RoomEyes.Found found, string label, bool size)
        {
            if (targetTag != null) Destroy(targetTag.gameObject);
            targetTag = Tag(found, size, label);
            Tags.Remove(targetTag);
        }

        private void ShowTargetTagAt(Vector3 point, string label)
        {
            if (targetTag != null) Destroy(targetTag.gameObject);
            targetTag = PaperTag("Seen " + label, .09f, .026f, Color.white, out var text, 11f); // F77: a paper tag, not a pill
            text.text = label;
            AddTail(targetTag, .026f, Color.white);
            targetTag.position = point + Vector3.up * .04f;
            ToyFx.Puff(point + Vector3.up * .01f, .01f, 3);
            ToySounds.Play(ToySounds.Cue.Pop, point, .4f, 1.3f);
        }

        private void HideTargetTag()
        {
            if (targetTag != null) Destroy(targetTag.gameObject);
            targetTag = null;
        }

        // F77 (the user: "New creation, not Start over"): said, it is asked first; the settings box keeps its second tap.
        public const string NewCreationLabel = "New creation", NewCreationQuestion = "Start a new creation?";
        private bool newCreationAsked;
        private (Crew who, string text, Reply[] replies, Step step)? beforeNewCreation;
        public bool NewCreationAsked => newCreationAsked;

        private void AskNewCreation()
        {
            newCreationAsked = true;
            // Keep going brings back what was being asked before (Finish | Change shape, say).
            var q = Question;
            beforeNewCreation = q != null && !q.answered && !q.toCrew && q.text != NewCreationQuestion
                ? (q.who, q.text, q.chips.Select(c => c.reply).ToArray(), Current) : ((Crew, string, Reply[], Step)?)null;
            Heard();
            AskWith(Crew.Maker, NewCreationQuestion, false, Rp(NewCreationLabel, "new creation"), Rp("Keep going", "keep going"));
        }

        private bool NewCreationVoice(string p)
        {
            bool asks = p.Contains("start over") || p.Contains("처음부터") || p.Contains("new creation") || p.Contains("새로 만들") || p == "make something new";
            bool onQuestion = Question != null && !Question.answered && Question.text == NewCreationQuestion;
            if (newCreationAsked && (p == "new creation" || (onQuestion && IsYes(p)) || asks))
            {
                newCreationAsked = false;
                CloseQuestion("new creation");
                StartOver();
                return true;
            }
            if (newCreationAsked && (p == "keep going" || (onQuestion && IsNo(p))))
            {
                newCreationAsked = false;
                CloseQuestion("keep going");
                var back = beforeNewCreation;
                beforeNewCreation = null;
                if (back.HasValue && back.Value.step == Current)
                {
                    Caption(back.Value.who, back.Value.text, 3600f);
                    OpenQuestionCard(back.Value.who, back.Value.text, false, back.Value.replies);
                }
                else Ack(Crew.Maker, "Okay, let's keep going!");
                return true;
            }
            if (!asks) return false;
            AskNewCreation();
            return true;
        }

        public void StartOver()
        {
            newCreationAsked = false;
            PutAside();
            // Coming back with the idea wall up: the next idea comes off its pad. Otherwise the guide
            // offers a fresh note again.
            if (board != null)
            {
                arrival.PutNoteOnPad(PadPose.position, PadPose.rotation, BoardScale);
                Say(Crew.Maker, "Fresh start! Peel a new note off the pad when you're ready.");
            }
            else arrival.RestartTopic();
        }

        // Whatever was going on is put aside (a finished make stays where it was put).
        private void PutAside()
        {
            if (Product != null && productFinished) Remember();
            RetireStands(); // F105: no stand outlives its work
            FlushNotebookSnapshot();
            ending = EndingState.Working;
            projectPlate = promptedProduct = endingLastProduct = null;
            endingLastDesign = null;
            CloseNotebook();
            StopAllCoroutines();
            ResetReview();
            ClearSizeBox();         // F75
            AsideInputsAndSculpt(); // F33: the show-me circle, the offered book, the clay or model in progress
            HideIdeasButton();      // F45
            ResetForObject();
            Voice.Stop();
            lines.Clear();
            working.Clear();
            ClearLooks();
            ClearIdeas();
            ClearTags();
            HideFitMarks();
            pendingChange = null;
            nextAsVersion = false;
            // A finished make stays where it was put; an unfinished one goes.
            if (Product != null)
            {
                if (productFinished && !Holding) { if (Kept != null && Kept != Product && !RoomItems.Contains(Kept)) RoomItems.Add(Kept); Kept = Product; if (Design != null) madeNames[Kept] = Design.name; }
                else Destroy(Product.gameObject);
                Product = null;
            }
            holdHand = null;
            mouseHolding = false;
            held = null;
            heldBody = null;
            PutBrushDown();
            awaitingFit = false;
            PutSketchbookAway();
            awaitingTidy = false;
            freeTape = false;
            ShowTape(false);
            CalmReset(); // F76
            HideCaption();
            foreach (var t in tags.Values) if (t != null) t.gameObject.SetActive(false);
            Begun = false;
            Style = Shape = Need = Material = "";
            Look = null;
            Design = null;
            Reference = null;
            Matched = Target = null;
            TargetName = null;
            HideTargetTag();
            HasMeasure = HasSpot = false;
            Hint = 0f;
            ResetShapes();
            InMeeting = false;
            Talk.Clear();
            ResetCrew();
            NewProject();
            DestroyDraft();      // F40
            ClearFlags();
            HidePrintMarks();    // F42
            foreach (var v in Versions) if (v.root != null && v.root != Kept) RoomItems.Add(v.root);
            Versions.Clear();
        }

        // F33: what used to be one "what vibe?" question is now the shape, then the colours (MakerSession.Inputs.cs).
        private void StartStyle() => StartShapeInput();

        // ------------------------------------------------------------ voice

        public bool OnVoice(string transcript)
        {
            if (!Begun) return false;
            string text = transcript?.Trim() ?? "";
            if (text.Length == 0) return true;
            string p = text.ToLowerInvariant().Trim().TrimEnd('.', '!', '?');
            Talk.Add("Person: " + text);
            Heard();
            // F48: every utterance is written down with where it went (maker_talk_log.jsonl).
            string before = SpeechState();
            voiceRoute = null;
            bool taken;
            personActing = true; // F54: what the person asks for now comes before a waiting teammate question
            try
            {
                // F55: the page in hand first ("done" while drawing in the shape review finishes the drawing).
                if (RePickVoice(p)) { voiceRoute = "change sketch"; taken = true; } // F81: a different sketch while measuring
                else if (SizeBoxVoice(text, p)) { voiceRoute = "size box"; taken = true; } // F75: "done" with the size box out makes it (not the ending)
                else if (ColourDoneVoice(p)) { voiceRoute = "colour done?"; taken = true; } // F95: Spark's "All done colouring?"
                else if (EndingVoice(p)) { voiceRoute = "completion"; taken = true; }
                else if (RewriteVoice(text, p)) { voiceRoute = "rewrite note"; taken = true; } // F72: the note on the wall waits for new words
                else if ((Drawing || OfferingTidy) && DrawingVoice(p)) { voiceRoute = "drawing"; taken = true; }
                else if (PolishVoice(text, p)) { voiceRoute = "polish drawing"; taken = true; } // F86: polish the handed drawing or keep it
                else if (ShapeRoleVoice(text, p)) { voiceRoute = "shape card role"; taken = true; } // F56: what the card on the note is for
                else if (ResizeVoice(text, p)) { voiceRoute = "auto-resize"; taken = true; } // F88: "auto resize", yes/no to Fixer's question
                else if (PartVoice(text, p)) { voiceRoute = "part"; taken = true; } // F83: "make the beak longer", "this is the tail"
                else if (SolidVoice(text, p)) { voiceRoute = "solid"; taken = true; } // F59: sculpting and cuts (before the review's "done")
                else if (FinishShapeVoice(text, p)) { voiceRoute = "finish shape"; taken = true; } // F63: a shape wish while colouring is not paint
                else taken = ReviewVoice(text, p) || (UseDirector ? DirectorVoice(text, p) : Route(text, p));
            }
            finally { personActing = false; }
            LogSpeech(text, before, voiceRoute ?? (taken ? "step: " + before : "not taken"));
            return taken;
        }

        // The keyword router: exact commands first, then questions to the crew, then what a plain sentence means
        // at this step. (F48: the scripted Editor preview and the checks always use it; with the Director on,
        // live speech goes through MakerSession.Director.cs instead.)
        private bool Route(string text, string p)
        {
            bool Took(string route) { voiceRoute = route; return true; }
            if (RePickVoice(p)) return Took("change sketch"); // F81
            if (SizeBoxVoice(text, p)) return Took("size box"); // F75
            if (ColourDoneVoice(p)) return Took("colour done?"); // F95
            if (EndingVoice(p)) return Took("completion");
            if (RewriteVoice(text, p)) return Took("rewrite note"); // F72
            if ((Drawing || OfferingTidy) && DrawingVoice(p)) return Took("drawing"); // F55: before the review takes "done"
            if (PolishVoice(text, p)) return Took("polish drawing"); // F86
            if (ShapeRoleVoice(text, p)) return Took("shape card role"); // F56
            if (ResizeVoice(text, p)) return Took("auto-resize"); // F88
            if (PartVoice(text, p)) return Took("part"); // F83
            if (SolidVoice(text, p)) return Took("solid"); // F59
            if (FinishShapeVoice(text, p)) return Took("finish shape"); // F63
            if (ReviewVoice(text, p)) return true;
            if (Current == Step.Measuring && SizeVoice(p)) return Took("size: you decide"); // F61
            // (F48) A teammate's own question is open: what is said now answers it, as its reply pills do.
            var open = Question;
            bool crewAsked = !answeringByReply && open != null && !open.answered && open.toCrew;
            VoiceAnswersQuestion(); // F44
            if (NewCreationVoice(p)) return Took("new creation"); // F77: asks first (New creation / Keep going)
            if (SettingsVoice(p)) return Took("settings");                                        // F55
            if (AsksWhatCrewDoes(p)) { Heard(); RollCall("asked"); return Took("roll call"); }    // F55
            if (awaitingFit && FitAnswer(p)) return Took("fit answer");
            if (NotebookVoice(p)) return Took("notebook");
            if (ToolsVoice(text, p)) return Took("tools");
            if (Make3DVoice(p)) return Took("pin up");
            if (TapeVoice(p)) return Took("tape");
            if (PrintCheckVoice(p)) return Took("print check");   // F42
            if (DraftVoice(text, p, crewAsked)) return Took("v2");  // F40: done, the confirm, sizes on v2
            // F77: Export is what a finished work does (the ending, then Export); before that it says how to get there.
            if (Current == Step.Done && (p.Contains("export") || p.Contains("내보내"))) { Caption(Crew.Maker, "Say I'm finished first. Then you can export it.", 6f); return Took("export before finishing"); }
            if (Current == Step.Done && (p.Contains("print") || p.Contains("출력"))) { Print(); return Took("print"); }
            if (p.Contains("what do you see") || p.Contains("뭐 보여") || p.Contains("뭐가 보여")) { StartCoroutine(NameThings()); return Took("name things"); }
            // F45: before anything is chosen, asking for ideas is the Find ideas button; "I don't know" only points at it.
            if (Current == Step.Suggesting && Ideas.Count == 0 && !InMeeting)
            {
                if (AsksForIdeas(p)) { FindIdeas("said"); return Took("find ideas"); }
                if (StuckWords(p)) { PointAtIdeasButton(); return Took("point at ideas"); }
            }
            if (Current != Step.Making && LooksAround(p)) { LookAroundNow(); return Took("look around"); }
            // (2026-11-12) "Can you see it?" / "take it" while the shape or colours are given: Spark's frame takes its picture.
            if ((InInputs || Current == Step.ForObject) && FrameShown && FrameSizeVoice(p)) return Took("frame size"); // F60b: "make the frame bigger", "phone size"
            if ((InInputs || Current == Step.ForObject) && FrameShown && SeeWords(p)) { TakeShowPicture(true); return Took("show frame"); }
            // F49: in the finish step "done"/"skip" and the vibe go to it (how/what/why questions still go to the crew).
            if (!crewAsked && FinishVoice(text, p)) return Took("finish");
            // (F48) The answer to a teammate's question goes back to the crew, not into the step ("yes, hot
            // coffee" used to become a flag on v2 or a new shape).
            if (crewAsked && Current != Step.Making && Current != Step.Planning && !QuestionLike(p) && !JustReaction(p))
            {
                CloseQuestion("accepted spoken answer");
                StartCoroutine(CrewTurn("said", "(answering " + MakerRoles.Name(open.who) + "'s question \"" + open.text + "\") " + text));
                return Took("answer to " + MakerRoles.Name(open.who));
            }
            // Questions, comparisons, opinions: the crew decides how to answer (F29).
            if (Current != Step.Making && AsksCrew(text, p) && !(Current == Step.Measuring && TryMillimetres(p, out _)))
            {
                StartCoroutine(CrewTurn("said", text));
                return Took("crew turn");
            }
            return StepInput(text, p);
        }

        // What a plain sentence means at the current step (also what the crew's "do" carries out).
        private bool StepInput(string text, string p)
        {
            switch (Current)
            {
                case Step.Suggesting:
                    if (TryPickIdea(p)) return true;
                    if (Ideas.Count > 0 && IsYes(p)) { PickIdea(Ideas[0]); return true; }
                    Need = text;
                    KindOverride = null;
                    SendOthersToWall(null);
                    RememberWallNote(text);
                    arrival.WriteTopic(MakerRoles.Clean(text)); // after a quiet start: the waiting note is written and goes into the ring
                    ClearIdeas();
                    Ack(Crew.Maker, "Got it!");
                    Note("brief", "What we're making", Need);
                    if (string.IsNullOrEmpty(Material)) StartCoroutine(Research());
                    AfterNeed(); // F45: the thing it is for goes in the circle first
                    return true;
                case Step.ForObject:
                    return ForObjectVoice(text, p);
                case Step.Finish:
                    return FinishVoice(text, p, true); // F49
                case Step.ShapeInput:
                case Step.ColourInput:
                    return InputVoice(text, p);
                case Step.Style:
                    Style = text;
                    if (PointsAtSomething(p)) StartCoroutine(MatchSomething(text));
                    else StartCoroutine(MakeLooks());
                    return true;
                case Step.Looks:
                    if (TryPickLook(p)) return true;
                    if (!LooksShown) { Ack(Crew.Maker, "Still sketching!"); return true; }
                    // F49: words redraw the round on screen: more about the shape, or about the vibe.
                    if (FormFirst && lookJob == "forms") { AddShapeRule(text); ShapeWords = (ShapeWords + ", " + text).Trim(' ', ','); StartCoroutine(MakeLooks("forms")); return true; } // F63: "no bumps" also holds later
                    if (FormFirst && lookJob == "finishes" && FinishShapeVoice(text, p)) return true; // F63: a shape wish is not paint
                    if (FormFirst && lookJob == "finishes" && ColouringVoice(p)) return true; // F87: "I'll paint it myself", "none of these"
                    if (FormFirst && lookJob == "finishes") { VibeWords = (VibeWords + ", " + text).Trim(' ', ','); StartCoroutine(MakeLooks("finishes")); return true; }
                    Style = (Style + ", " + text).Trim(' ', ',');
                    if (PointsAtSomething(p)) StartCoroutine(MatchSomething(text));
                    else StartCoroutine(MakeLooks());
                    return true;
                case Step.Measuring:
                    if (Kind == "tray") return TraceVoice(p);
                    if (TryMillimetres(p, out float mm)) { SetMeasure(mm, true); ConfirmMeasure(); return true; }
                    if (IsYes(p) || p.Contains("use yours") || p.Contains("your guess"))
                    {
                        if (!HasMeasure && Hint > 0f) SetMeasure(Hint, true);
                        if (HasMeasure) ConfirmMeasure();
                        return true;
                    }
                    if (p.Contains("again") || p.Contains("다시")) { StopSettle(); HasMeasure = false; Tape.Reset(); ShowTape(false); Say(Crew.Fixer, "Okay. Pinch the edges again."); return true; }
                    Ack(Crew.Fixer, "Pinch both edges and pull apart.");
                    return true;
                case Step.Shape:
                    if (LateOkay(p)) return true;
                    Shape = text;
                    StartCoroutine(Plan(""));
                    return true;
                case Step.Planning:
                case Step.Making:
                    // It is being worked out or built right now: a change comes right after it.
                    if (MakeNow(p)) { Ack(Crew.Maker, Current == Step.Making ? "Building! Hang on." : "Building it right away!"); return true; }
                    // (F48) A question or a "wow" is not a change: it used to be kept and, once the thing was made,
                    // redrawn and sent to Meshy again.
                    if (AsksCrew(text, p) || QuestionLike(p) || JustReaction(p)) { SayProgress(JustReaction(p)); voiceRoute = "making: not a change"; return true; }
                    pendingChange = text;
                    Ack(Crew.Maker, "Got it, right after this!");
                    return true;
                default:
                    if (p.Contains("another") || p.Contains("new one") || p.Contains("다른")) { StartOver(); return true; }
                    // F54: right after a make there is no see-through v2 yet: a change said now is Change it plus that
                    // change (never a rebuild of the made thing); a "wow" or a question stays what it is.
                    if (Draft == null && FormFirst && Review == ReviewStage.Shape)
                    {
                        if (JustReaction(p)) { Body(Crew.Spark)?.Play("React_Nod"); voiceRoute = "review: a reaction, no change"; return true; }
                        if (QuestionLike(p)) { if (!crewBusy) StartCoroutine(CrewTurn("said", text)); voiceRoute = "review: a question, no change"; return true; }
                        EnterChangeMode();
                    }
                    // F40: with v2 drafted, what the person says becomes their own flag on it (planted flags change it).
                    // (F48) Only a change is a flag: "can you make version two?" asks for v2, "thanks" is thanks,
                    // and a question goes to the crew.
                    if (Draft != null)
                    {
                        if (WantsV2(p)) { StartConfirm(); voiceRoute = "v2: make it"; return true; }
                        if (JustReaction(p)) { Body(Crew.Spark)?.Play("React_Nod"); voiceRoute = "v2: a reaction, no flag"; return true; }
                        if (QuestionLike(p)) { if (!crewBusy) StartCoroutine(CrewTurn("said", text)); voiceRoute = "v2: a question, no flag"; return true; }
                        PersonFlag(text);
                        voiceRoute = "v2: the person's flag";
                        return true;
                    }
                    // It is already made: "make it" (or a plain yes) changes nothing.
                    if (MakeNow(p)) { Ack(Crew.Maker, IsYes(p) ? "Yay!" : "It's right here! Paint it, or say print."); return true; }
                    if (WantsVersion(p)) nextAsVersion = true;
                    Shape = (Shape + ", " + text).Trim(' ', ',');
                    StartCoroutine(Plan(text));
                    return true;
            }
        }

        public static bool TryMillimetres(string p, out float mm)
        {
            mm = 0f;
            var m = Regex.Match(p, @"(\d+(?:[.,]\d+)?)\s*(mm|millimet|밀리|cm|centimet|센티)");
            if (!m.Success) return false;
            float value = float.Parse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            string unit = m.Groups[2].Value;
            mm = unit.StartsWith("c") || unit.StartsWith("센") ? value * 10f : value;
            return mm >= 10f && mm <= 2000f;
        }

        // A change kept as the next version beside the last one (a flag does the same).
        public static bool WantsVersion(string p) =>
            new[] { "new version", "another version", "next version", "keep both", "keep the old", "새 버전", "다음 버전", "버전으로" }.Any(p.Contains);

        // "Make it" on its own (not "make it blue" or "make it bigger": those are changes).
        private static bool MakeNow(string p) =>
            IsYes(p) || p == "make it" || p == "make it now" || p == "let's make it" || p == "build it" || p == "build" || p == "go ahead and make it" ||
            p.EndsWith("만들어") || p.EndsWith("만들어줘") || p.EndsWith("만들자");

        private static bool IsYes(string p) =>
            p == "yes" || p == "yeah" || p == "yep" || p == "ok" || p == "okay" || p == "sure" || p == "done" || p == "good" || p.Contains("do it") ||
            p.Contains("looks good") || p.Contains("perfect") || p.Contains("go ahead") || p.Contains("that one") || p == "응" || p == "네" || p == "좋아" || p.Contains("맞아");

        public static bool LooksAround(string p) =>
            AsksForIdeas(p) || new[] { "interesting", "in my room", "재밌는", "흥미" }.Any(p.Contains);

        // F45: the person asks the crew to look for ideas (the same as pressing Find ideas). "I don't know what to
        // make" is not asking (StuckWords): the crew points at the button and never looks around unasked.
        public static bool AsksForIdeas(string p) =>
            new[] { "look around", "find ideas", "find me ideas", "find some ideas", "find an idea", "find something", "find anything", "find me something",
                    "explore", "surprise me", "give me ideas", "give me some ideas", "give me an idea", "any ideas", "any idea", "help me think", "help me find",
                    "what should i make", "what can i make", "what should we make", "what can we make", "suggest something", "suggest some", "suggest an idea",
                    "inspir", "around my room", "around the room", "go find", "go look", "둘러봐", "둘러 봐", "찾아봐", "찾아 봐", "찾아줘", "찾아 줘", "추천해",
                    "뭐 만들까", "뭘 만들까", "아이디어 줘", "아이디어 좀", "탐색", "구경", "영감" }.Any(p.Contains);

        // "like this", "match my lamp", "this picture on my phone", "이거처럼"...
        public static bool PointsAtSomething(string p) =>
            new[] { "this", "that", "like my", "match", "same colo", "look at", "see the", "screen", "monitor", "phone", "picture", "이거", "이것", "저거", "같은 색", "처럼", "비슷", "화면", "사진" }.Any(p.Contains);

        private bool TryPickLook(string p)
        {
            // (F48: "one" alone used to pick the first look, so "I like the blue one" did.)
            string[][] words = { new[] { "first", "left", "number one", "look one", "picture one" }, new[] { "second", "middle" }, new[] { "third", "right", "last" } };
            for (int i = 0; i < Looks.Count && i < 3; i++)
                if (words[i].Any(w => Regex.IsMatch(p, @"\b" + w + @"\b")) || (i == 0 && p == "one") || (Looks[i].look != null && p.Contains(Looks[i].look.label.ToLowerInvariant())))
                {
                    PickLook(Looks[i]);
                    return true;
                }
            return false;
        }

        private bool TryPickIdea(string p)
        {
            // Not "coaster"/"holder": "a pen holder" is a new idea. F30: three notes, or the teammate's name.
            string[][] words =
            {
                new[] { "first", "left", "that one", "this one" }, new[] { "second", "middle", "other one" }, new[] { "third", "right", "last" }
            };
            for (int i = 0; i < Ideas.Count && i < 3; i++)
            {
                var idea = Ideas[i];
                string by = MakerRoles.Name(idea.who).ToLowerInvariant();
                if (words[i].Any(w => Regex.IsMatch(p, @"\b" + w + @"\b")) || p.Contains(idea.idea.label.ToLowerInvariant()) || Regex.IsMatch(p, @"\b" + by + @"('s)?\b"))
                {
                    PickIdea(idea);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------ 0. the idea meeting (MakerSession.Spots.cs)

        private static RoomEyes.Found Locate(string name, List<RoomEyes.Found> seen)
        {
            if (seen == null || string.IsNullOrWhiteSpace(name)) return null;
            string n = name.ToLowerInvariant();
            return seen.FirstOrDefault(f => f.seen.name.ToLowerInvariant() == n) ?? seen.FirstOrDefault(f => f.seen.name.ToLowerInvariant().Contains(n) || n.Contains(f.seen.name.ToLowerInvariant()));
        }

        public void PickIdea(Choice choice)
        {
            if (Current != Step.Suggesting || choice == null) return;
            Need = MakerRoles.Clean(choice.idea.need);
            // F30: what kind of thing the note was for, and its spot (the desk edge for a clip).
            KindOverride = Array.IndexOf(FitShapes.Kinds, choice.idea.kind ?? "") >= 0 ? choice.idea.kind : null;
            if (choice.spot != null && choice.spot.kind == "edge") SetEdge(choice.spot.point, choice.spot.outward);
            SendOthersToWall(choice);
            // F79 (Quest: "my note is not on the wall"): the picked idea is written on the person's own note, which goes up
            // as theirs (it used to go up in the teammate's colour and face, so the person's own yellow note seemed missing).
            RememberWallNote(MakerRoles.Clean(choice.idea.label), null, choice.idea.kind);
            arrival.WriteTopic(MakerRoles.Clean(choice.idea.label)); // after a quiet start: the waiting note is written and goes into the ring
            Target = choice.found;
            Talk.Add("Person picked: " + choice.idea.label);
            Heard();
            Note("brief", "What we're making", MakerRoles.Clean(choice.idea.label) + ": " + Need);
            Hush();
            ToySounds.Play(ToySounds.Cue.Pop, choice.root.position, .6f, 1.1f);
            ToyFx.Puff(choice.root.position, .012f, 4);
            ClearIdeas();
            Ack(Crew.Spark, "Yes!");
            AfterNeed(); // F45: an idea for a thing Spark already saw goes straight on; else its thing goes in the circle
        }

        // ------------------------------------------------------------ 1. vibe -> three looks

        // "Make it like this": Spark finds the object (or a picture on a screen) and takes its look.
        private IEnumerator MatchSomething(string words)
        {
            Current = Step.Looks;
            Ack(Crew.Spark, "Let me see it!");
            RoomEyes.Found found = null;
            yield return SparkLooks(words, list => found = list.FirstOrDefault(f => f.seen.focus));
            if (found == null)
            {
                Say(Crew.Spark, Eyes.LastError ?? "I can't spot it. Look right at it and say that again.");
                BackToColours();
                yield break;
            }
            Matched = found;
            Reference = found.crop;
            ClearTags();
            HideSeenLabel(found);
            if (found.placed) Tag(found, false);
            string colors = string.Join(", ", found.seen.colors ?? new string[0]);
            Style = words + " (match the " + MakerRoles.Clean(found.seen.name) + ": colours " + colors + "; surface " + MakerRoles.Clean(found.seen.surface) + ")";
            Talk.Add("Spark saw: " + found.seen.name + ", " + colors + ", " + found.seen.surface);
            Note("palette", "Colours from your " + MakerRoles.Clean(found.seen.name), colors + "; " + MakerRoles.Clean(found.seen.surface), found.crop);
            Say(Crew.Spark, $"Got it, your {MakerRoles.Clean(found.seen.name)}. {Capital(MakerRoles.Clean(found.seen.surface))}!");
            yield return MakeLooks();
        }

        // F49: `job` is the round: "forms" (white clay shape sketches before measuring), "finishes" (colour and
        // material versions of the made thing) or "looks" (F33). Without it the current round is drawn again.
        private IEnumerator MakeLooks(string job = null)
        {
            lookJob = job ?? (FormFirst && (lookJob == "forms" || lookJob == "finishes") ? lookJob : "looks");
            string madeLook = Look != null ? MakerRoles.Clean(Look.label) + ": " + Look.prompt : Need;
            Current = Step.Looks;
            ClearLooks();
            Look = null;
            LooksShown = false;
            sketchShapes.Clear();
            // Until the pictures are done there is nothing to see but Maker drawing in its book; then
            // they all pop out of the book at once, in a row just above the crew.
            Ack(Crew.Maker, "Sketching!");
            if (!Sketching) StartCoroutine(Sketch());
            string json = null;
            var formImages = lookJob == "forms" ? FormRefs().Select(r => r.jpeg).ToList() : null; // F60: Maker looks at them
            LastFormImages = formImages?.Count ?? 0;
            if (lookJob == "forms")
                yield return Ask(Crew.Maker, "forms", new { need = Need, shape = ShapeBrief(), rules = ShapeRules.ToArray(), forObject = TargetName ?? "", talk = Talk.ToArray() }, r => json = r, false, formImages);
            else if (lookJob == "finishes")
            {
                // The textures picked for it are cleaned first (only the material goes to the picture model).
                for (float wait = Time.time + 45f; CleaningTexture && Time.time < wait && Current == Step.Looks; ) yield return null;
                if (Current != Step.Looks) yield break;
                string made = Scrub(madeLook, BannedStems()); // F63: a detail a rule forbids is not described again
                yield return Ask(Crew.Maker, "finishes", new { need = Need, made, rules = ShapeRules.ToArray(), vibe = VibeWords, palette = FinishSwatches.Select(s => s.kind + " " + s.name + " " + s.hex).ToArray(), talk = Talk.ToArray() }, r => json = r, false,
                    MoodPictures.Count > 0 ? MoodPictures.ToList() : null); // F87: Maker sees the mood photos
            }
            // F33: the shape the person gave (words; a photo or a drawing goes to the picture model as an image)
            // and the colours (words, palette swatches) in `style`.
            else yield return Ask(Crew.Maker, "looks", new { need = Need, shape = ShapeBrief(), rules = ShapeRules.ToArray(), style = Style, talk = Talk.ToArray() }, r => json = r, false);
            if (Current != Step.Looks) yield break;
            if (json == null) { sketchWanted = false; BackToColours(); yield break; }
            var reply = JsonConvert.DeserializeObject<LooksReply>(json);
            var looks = (reply.looks ?? new MakerLook[0]).Take(3).ToList();
            if (lookJob != "finishes") TakeSizePlan(reply.sizes); // F61: the sizes the measuring step asks
            // F60: Maker says what it sees in the person's pictures, so a wrong reading can be fixed at once (words in
            // the Looks step redraw the sketches).
            if (lookJob == "forms" && !string.IsNullOrWhiteSpace(reply.seen) && formImages != null && formImages.Count > 0)
                Say(Crew.Maker, "I see " + MakerRoles.Clean(reply.seen).TrimEnd('.') + ". Sketching that!");
            sketchShapes.AddRange(looks.Select(l => SketchShape(l.label + " " + l.prompt)));
            Vector3 center = AboveCrew(LooksLift, out Vector3 side);
            for (int i = 0; i < looks.Count; i++)
            {
                var picture = MakePicture(.15f, MakerRoles.Clean(looks[i].label));
                picture.look = looks[i];
                picture.home = center + side * (i - (looks.Count - 1) * .5f) * .19f; // KeepRowOverBubble moves the row onto Maker's bubble
                picture.root.gameObject.SetActive(false);
                Looks.Add(picture);
                StartCoroutine(Draw(picture, looks[i].prompt, i));
            }
            float until = Time.time + 150f;
            while (Current == Step.Looks && Looks.Any(l => !l.Done) && Time.time < until) yield return null;
            if (Current != Step.Looks) yield break;
            foreach (var failed in Looks.Where(l => !l.Ready).ToList()) { if (failed.root != null) Destroy(failed.root.gameObject); Looks.Remove(failed); }
            Vector3 from = sketchPad != null && sketchPad.gameObject.activeSelf ? sketchPad.position : AboveCharacter(Crew.Maker, .1f);
            sketchWanted = false;
            if (Looks.Count != 3)
            {
                ClearLooks();
                Say(Crew.Maker, "I couldn't finish all three pictures. Tell me again to retry.");
                BackToColours();
                yield break;
            }
            yield return RevealLooks(from);
            // Everyone has an opinion: one sticker each (they may disagree), then Maker hands over.
            yield return CrewTurn("looks_shown", "");
            if (Current != Step.Looks) yield break;
            // F61: the turn ends with Maker's one line (its pick and why) and the pick prompt; no second prompt.
        }

        public void PickLook(Picture picture)
        {
            if (Current != Step.Looks || picture == null || !picture.Ready || picture.root == null || !picture.root.gameObject.activeSelf) return;
            Look = picture.look;
            LookJpeg = picture.jpeg;
            if (lookJob == "forms") FormJpeg = picture.jpeg; // F49: what the finish pictures change later
            Talk.Add("Person picked the look: " + Look.label);
            Hush();
            Heard();
            ClearTabs();
            Note("look", "Chosen look", MakerRoles.Clean(Look.label), picture.jpeg);
            ToySounds.Play(ToySounds.Cue.Pop, picture.root.position, .6f, 1.1f);
            ToyFx.Puff(picture.root.position, .012f, 4);
            // The chosen picture stays as a small reference beside the work; the others go.
            bool pinned = false;
            bool keepAside = lookJob != "finishes"; // F81: kept (hidden) so the pick can be changed while measuring
            LoadPins(); // F88: count the pins this pick puts up (Change sketch takes them down again)
            int pinsBefore = Pins.Count, viewsBefore = PinViews.Count;
            foreach (var other in Looks.Where(l => l != picture).ToList())
            {
                if (other.Ready && other.root != null) { PinUp(other.jpeg, other.look?.label ?? "Look", other.root.position); pinned |= hasWall || (NoteOnWall && board != null); }
                if (keepAside) continue;
                Destroy(other.root.gameObject);
                Looks.Remove(other);
            }
            if (keepAside) { KeepOthersAside(picture); RememberRowPins(pinsBefore, viewsBefore); }
            if (pinned && !pickingAgain) Say(Crew.Spark, "I pinned the others on your wall, for later."); // F88: said once, not on every re-pick
            pickingAgain = false;
            picture.root.localScale = Vector3.one * (ChosenAtEyes ? ChosenScale : .7f);
            picture.home = ChosenAtEyes ? ChosenHome(picture) : BesideMaker();
            if (ChosenAtEyes && Head != null) WriteInputLog("chosen picture: " + ChosenPlacedHow + ", " + ((Head.position.y - picture.home.y) * 100f).ToString("0") + " cm under the eyes", true);
            // F49: a finish picked: Maker paints the made thing in it (no measuring; it is already made).
            if (FormFirst && lookJob == "finishes") { StartCoroutine(Repaint(picture)); return; }
            StartMeasuring();
        }

        // ------------------------------------------------------------ 2. the person measures

        private void StartMeasuring()
        {
            if (StartShapeMeasuring()) return; // F30: trays trace, clips measure the desk edge, models a width
            Current = Step.Measuring;
            HasMeasure = false;
            Hint = 0f;
            Tape.Reset();
            ShowTape(false);
            freeTape = false;
            Say(Crew.Fixer, Kind == "vessel" ? "Your turn. Pinch both sides of what goes inside it and pull your hands apart."
                : "Your turn. Show me how wide it should be: pinch both edges and pull your hands apart."); // F61: no fixed mug
            if (!Taught(TapeTaughtKey)) StartCoroutine(TeachTape());
            else OfferFreeMeasure();
            StartCoroutine(SparkGuess());
        }

        // Spark's camera guess: a hint and the mug's spot, never the measurement itself.
        private IEnumerator SparkGuess()
        {
            RoomEyes.Found found = Target != null && Target.onSurface ? Target : null;
            if (found == null && Eyes != null && !Eyes.Looking)
                yield return SparkLooks("the object the person wants to make something for: " + Need, list => found = list.FirstOrDefault(f => f.seen.focus));
            if (Current != Step.Measuring || found == null || !found.onSurface || found.width < .02f || found.width > 1.5f) yield break;
            Target = found;
            Hint = Mathf.Round(found.width * 1000f);
            if (!HasSpot) { MugSpot = found.basePoint; HasSpot = true; }
            ClearTags();
            HideSeenLabel(found);
            if (targetTag != null && TargetName != null) ShowTargetTag(found, TargetName, true);
            else Tag(found, true);
            if (!HasMeasure) Say(Crew.Spark, $"Looks about {Hint:0} millimetres to me. Measure it, or say use yours.");
        }

        // spoken: a said size or Spark's guess, and the caller goes on at once. Otherwise (the hands, or an earlier
        // measurement Fixer offers) it goes on by itself after a short grace (MakerSession.TapeSettle.cs).
        private void SetMeasure(float mm, bool spoken, bool offered = false)
        {
            Measured = Mathf.Round(Mathf.Clamp(mm, 10f, 2000f) * 2f) / 2f;
            HasMeasure = true;
            if (!spoken) BoxPendingFromTape(); // F75: with the size box out, the hands measured one of its sides
            if (spoken)
            {
                Vector3 c = HasSpot ? MugSpot + Vector3.up * .006f : FrontPoint(.4f, -.3f, out _);
                Vector3 side = Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(c - (Head != null ? Head.position : Vector3.zero), Vector3.up).normalized);
                Tape.SetMeasured(c - side * Measured * .0005f, c + side * Measured * .0005f);
            }
            else if (KeepsSpot()) { } // F30: a clip stays on its edge, a model where it stands
            else
            {
                // The tape lies across the mug's foot: its middle is where the mug stands (unless the
                // hands measured somewhere else, away from the mug Spark found).
                Vector3 mid = (Tape.A + Tape.B) * .5f;
                Vector3 onDesk = new Vector3(mid.x, DeskHeightAt(mid, Mathf.Min(Tape.A.y, Tape.B.y)), mid.z);
                bool nearTarget = Target == null || !Target.onSurface || Vector3.ProjectOnPlane(onDesk - Target.basePoint, Vector3.up).magnitude < .2f;
                MugSpot = nearTarget ? onDesk : Target.basePoint;
                HasSpot = true;
            }
            ShowTape(true);
            UpdateTapeVisual();
            ToySounds.Play(ToySounds.Cue.Chime, (Tape.A + Tape.B) * .5f, .6f, 1.25f);
            if (Current == Step.Measuring && Kind == "tray") { TraceMeasured(spoken); return; } // F30: sizes the traced things
            if (spoken || Current != Step.Measuring) { StopSettle(); Ack(Crew.Fixer, $"{Measured:0.#} millimetres. Got it!"); return; }
            if (offered) Caption(Crew.Fixer, $"Using your {Measured:0.#} millimetres from before. Measure again if not.", OfferedSettleSeconds + .5f);
            else SettleHint(); // F61: the first time, it can still be changed before the bar fills
            StartSettle(offered ? OfferedSettleSeconds : SettleSeconds);
        }

        public void ConfirmMeasure()
        {
            if (Current == Step.Measuring && Kind == "tray") { TraceConfirm(); return; }
            if (Current != Step.Measuring || !HasMeasure) return;
            if (SizeBoxPhase == BoxPhase.Ready) { BoxConfirm(); return; } // F75: a side measured again, or Done
            StopSettle();
            measureConfirmedAt = Time.time;
            Talk.Add("Measured: " + Measured.ToString("0.#") + " mm");
            Hush();
            Heard();
            Note("measure", "Measured" + (TargetName != null ? " your " + TargetName : ""), Measured.ToString("0.#") + " mm " + (CurrentSize != null ? CurrentSize.label.Replace("how ", "") : "across"));
            if (!HasSpot) { MugSpot = DeskPointInFront(); HasSpot = true; }
            if (SizeBoxPhase == BoxPhase.None) PinTape(); // F39: the line stays where it was measured for a while (F75: the box keeps it)
            ClearTags();
            if (NextSize()) return; // F61: the next size this object needs
            if (BoxReadyNow()) return; // F75: the box waits for Done
            if (AfterMeasure()) return; // F30: the clip's type, or Meshy for a model
            Current = Step.Shape;
            if (Kind == "vessel") AskWith(Crew.Maker, "Straight, tapered or curvy? Or say let me draw.", false, Rp("Straight"), Rp("Tapered"), Rp("Curvy"), Rp("Let me draw"));
            else AskWith(Crew.Maker, "Round, hexagon, or something fun?", false, Rp("Round"), Rp("Hexagon"), Rp("Something fun"));
        }

        private void UpdateTape()
        {
            if (tapeDemo) return;
            if (Current != Step.Measuring) { UpdateFreeTape(); return; }
            Hand left = null, right = null;
            foreach (var hand in HandSet.Both())
                if (hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand)) { if (hand.Handedness == Handedness.Left) left = hand; else right = hand; }
            if (left == null && right == null) return;
            bool p0 = left != null && HandIO.Pinching(left), p1 = right != null && HandIO.Pinching(right);
            Vector3 a0 = left != null ? PinchPoint(left) : Vector3.zero, a1 = right != null ? PinchPoint(right) : Vector3.zero;
            Vector3 t0 = left != null ? Tip(left) : a0, t1 = right != null ? Tip(right) : a1;
            SizeBoxHands(left, right, ref p0, ref p1, a0, a1); // F75: a hand on the size box (or its knob) is not a tape end
            TapeGate(left, right, ref p0, ref p1, a0, a1); // F66: Fixer tosses the tape over first
            Tape.Step(Time.time, p0, a0, t0, p1, a1, t1);
            if (Tape.State == HandTape.Mode.TwoHands) SnapTapeEnds(); // F39: axis, edges, desk top
            bool live = Tape.State == HandTape.Mode.TwoHands || Tape.State == HandTape.Mode.Measured;
            ShowTape(live);
            if (live) UpdateTapeVisual();
            if (Tape.JustMeasured) SetMeasure(Tape.Length * 1000f, false);
        }

        // ------------------------------------------------------------ 3. shape -> on the real mug, checked

        private IEnumerator Plan(string change)
        {
            if (!revisionExecuting) ClearFlags(true); // F40: flags set aside stay for the next round
            if (Kind == "vessel") { yield return VesselPlan(change); yield break; }
            if (Kind == "tray") { yield return TrayPlan(change); yield break; }
            if (Kind == "clip") { yield return ClipPlan(change); yield break; }
            if (Kind == "model") { yield return ModelPlan(change); yield break; }
            MugTurn = Quaternion.identity;
            Current = Step.Planning;
            Hush();
            Ack(Crew.Maker, change.Length > 0 ? "On it!" : "Ooh, nice.");
            string json = null;
            yield return Ask(Crew.Maker, "design", new { need = Need, style = Style, look = Look, mugBaseMm = Measured, shape = Shape, change, talk = Talk.ToArray() }, r => json = r);
            if (json == null) { pendingChange = null; Current = Product != null ? Step.Done : Step.Shape; yield break; }
            var reply = JsonConvert.DeserializeObject<DesignReply>(json);
            var design = reply.design ?? new CoasterDesign();
            design.cupDiameter = Measured;
            if (Look != null && string.IsNullOrWhiteSpace(design.name)) design.name = Look.label;
            if (DrawnOutline != null && (Shape.Contains("drawn") || Shape.Contains("own") || change.Contains("drawn"))) { design.shape = "drawn"; design.outline = DrawnOutline; }
            if (string.IsNullOrEmpty(design.color) && Design != null) design.color = Design.color;
            Design = design.Clamped();
            Note("design", Design.name, Design.Summary);
            Say(Crew.Maker, MakerRoles.Clean(reply.say));
            yield return FitAndBuild();
        }

        // ------------------------------------------------------------ 4. fixed, then built right there

        // Since 2026-11-11 (after the Quest try) there is no see-through preview and no "make it": once the
        // design is decided, Fixer checks the plan and fixes it (the app computes the fixes, not the AI),
        // Maker builds the real thing where it goes, and Fixer says how it fits, on the made thing. A spoken
        // change rebuilds it in place and its paint stays; a flag (or "new version") keeps the last one
        // beside it as v1, v2... A change said while it is being worked out or built comes right after it.
        private bool nextAsVersion;
        private string pendingChange;
        private float gapUntil;
        public bool ChangeWaiting => pendingChange != null;

        private IEnumerator FitAndBuild()
        {
            if (Design == null) yield break;
            Current = Step.Making;
            working.Add(Crew.Fixer);
            if (!BodyWork(Crew.Fixer, "Work_Loop")) Cue(Crew.Fixer, "thinking");
            yield return new WaitForSeconds(Scripted ? .3f : .7f);
            var fixes = FixDesign(out string verdict);
            FixedByFixer = fixes.Count > 0;
            if (FixedByFixer)
            {
                Say(Crew.Fixer, Capital(fixes[0]) + ". Fixing.");
                for (int i = 0; i < 3; i++) { ToySounds.Play(ToySounds.Cue.Press, MugSpot, .5f, 1.6f + i * .1f); yield return new WaitForSeconds(.18f); }
                Talk.Add("Fixer fixed: " + string.Join(", ", fixes));
                Note("fix", "Fixer fixed it", Capital(string.Join(", ", fixes)) + (Design.IsFitShape ? "." : ". Gap now " + Gap.ToString("0.#") + " mm."));
            }
            working.Remove(Crew.Fixer);
            if (!BodyDone(Crew.Fixer)) Cue(Crew.Fixer, "idle");
            bool first = Product == null;
            bool asVersion = nextAsVersion && Product != null && productFinished;
            nextAsVersion = false;
            yield return Build(asVersion);
            if (Product == null) yield break;
            // Fixer's verdict on the made thing, with the gap beside it.
            ShowGapCard();
            if (!BodyDone(Crew.Fixer)) Cue(Crew.Fixer, "encourage");
            Say(Crew.Fixer, verdict);
            if (FormFirst && !ShapeConfirmed)
            {
                EnterShapeReview();
                yield break;
            }
            if (FormFirst && !finishDone) { StartFinish(); yield break; }
            StartCoroutine(CheckPrintable(Product, ProductMesh, Design)); // F42
            if (first)
            {
                PrintWords(out string printMaterial, out float minutes);
                Say(Crew.Maker, "Done! Paint it, move it, or tell me what to change.");
                Say(Crew.Fixer, $"Print it in {printMaterial}, about {minutes:0} minutes. Say print.");
                Say(Crew.Spark, "I'll remember where it lives!");
            }
            else Say(Crew.Maker, asVersion ? $"v{Versions.Count} is ready, next to the last one." : "Done! Changed it right here.");
            string json = null;
            object asked = Design.IsFitShape ? new { need = Need, design = Design.Summary, material = Material } : (object)new { need = Need, mugBaseMm = Measured, design = Design, material = Material };
            yield return Ask(Crew.Fixer, "check", asked, r => json = r, false);
            if (json != null && Current == Step.Done) Say(Crew.Fixer, MakerRoles.Clean(JsonConvert.DeserializeObject<NoteReply>(json).say));
            Cue(Crew.Fixer, "idle");
            // Something said while it was being worked out or built is done now.
            if (pendingChange != null && Current == Step.Done)
            {
                string change = pendingChange;
                pendingChange = null;
                if (WantsVersion(change.ToLowerInvariant())) nextAsVersion = true;
                Shape = (Shape + ", " + change).Trim(' ', ',');
                StartCoroutine(Plan(change));
            }
        }

        // Fixer's check of the plan, computed by the app: what it fixes before anything is built, and what
        // it says once it is made. Coasters and vessels: the gap around the real foot, the rim and the base.
        private List<string> FixDesign(out string verdict)
        {
            var d = Design.Copy();
            var fixes = new List<string>();
            switch (d.kind)
            {
                case "tray":
                {
                    float gap = d.clearance * .5f, wanted = 1.2f + fitBias;
                    if (gap < .9f + fitBias) { d.clearance = wanted * 2f; fixes.Add("the pockets are too tight"); }
                    else if (gap > 3.5f) { d.clearance = wanted * 2f; fixes.Add("the pockets are too loose"); }
                    if (d.baseThickness < 2f) { d.baseThickness = 2f; fixes.Add("thin floor"); }
                    if (d.rimHeight < 6f) { d.rimHeight = 6f; fixes.Add("low walls"); }
                    break;
                }
                case "clip":
                {
                    float gap = d.clearance * .5f;
                    if (gap < .2f) { d.clearance = .8f; fixes.Add("too tight to slide on"); }
                    else if (gap > 1.2f) { d.clearance = 1f; fixes.Add("loose, it would wobble"); }
                    if (d.rimWidth < 4f) { d.rimWidth = 4f; fixes.Add("thin bars"); }
                    if (d.clipType == "hook" && d.rimHeight < 25f) { d.rimHeight = 30f; fixes.Add("short hook"); }
                    break;
                }
                case "model":
                {
                    if (d.cut <= 0f && FitShapes.BaseContact(d) < .12f)
                    {
                        d.cut = Mathf.Clamp(d.modelHeight * .03f, 1.5f, 8f);
                        d.modelHeight -= d.cut;
                        fixes.Add("a wobbly base");
                    }
                    break;
                }
                default:
                {
                    float gap = d.clearance * .5f, wanted = 1.25f + fitBias;
                    if (gap < .9f + fitBias) { d.clearance = wanted * 2f; fixes.Add("too tight"); }
                    else if (gap > 3.5f) { d.clearance = wanted * 2f; fixes.Add("too loose"); }
                    if (d.rimWidth < 3f) { d.rimWidth = 3f; fixes.Add("thin rim"); }
                    if (d.baseThickness < 2.4f) { d.baseThickness = 2.4f; fixes.Add("thin base"); }
                    if (d.rimHeight > 0f && d.rimHeight < 2f) { d.rimHeight = 2f; fixes.Add("low rim"); }
                    break;
                }
            }
            if (fixes.Count > 0) Design = d.Clamped();
            Gap = Design.kind == "model" ? 0f : Design.clearance * .5f;
            switch (Design.kind)
            {
                case "tray":
                    verdict = $"{Gap:0.#} millimetres around each pocket. Your things fit.";
                    gapLabel.text = Gap.ToString("0.#") + " mm gap";
                    break;
                case "clip":
                    verdict = $"{Gap:0.#} millimetre grip on your {Design.cupDiameter:0} millimetre edge. It clicks on.";
                    gapLabel.text = Gap.ToString("0.#") + " mm grip";
                    break;
                case "model":
                {
                    // F75: with a width and a depth given (the size box), say both (the wider one was said as "wide").
                    // F88b: as the person sees it (a model turned a quarter has its own x front to back).
                    float wide = PersonWidthMm(Design), depth = PersonDepthMm(Design);
                    bool deep = depth > 0f && Mathf.Abs(depth - wide) >= 1f;
                    verdict = deep ? $"{wide:0} wide, {depth:0} deep, {Design.modelHeight:0} tall, in millimetres. It stands."
                                   : $"{wide:0} millimetres wide, {Design.modelHeight:0} tall. It stands.";
                    gapLabel.text = deep ? $"{wide:0} x {depth:0} x {Design.modelHeight:0} mm" : wide.ToString("0") + " mm wide";
                    break;
                }
                default:
                    verdict = $"{Gap:0.#} {(Mathf.Abs(Gap - 1f) < .05f ? "millimetre" : "millimetres")} all around. It fits.";
                    gapLabel.text = Gap.ToString("0.#") + " mm gap";
                    break;
            }
            return fixes;
        }

        // Maker builds the current design layer by layer. It replaces the made thing where that is now (the
        // person may have moved it) and keeps its paint; with `asVersion` the last one steps aside instead
        // and the new one (with the same paint) goes on the spot.
        private bool revealedBySculpt;
        private IEnumerator Build(bool asVersion)
        {
            FinishRevisionCommit();
            var old = Product;
            var paint = old != null ? old.GetComponentInChildren<PaintableThing>()?.SavedMarks() : null;
            if (dropPaintOnce) { paint = null; dropPaintOnce = false; } // F49: a new finish covers the finger paint
            bool inPlace = old != null && !asVersion;
            string replacedId = inPlace ? SavedProductId(old) : null;
            // F40: v2 made from the draft stands where the draft stood, and v1 stays where it is.
            bool fromDraft = asVersion && versionSpot != null;
            Vector3 at = inPlace ? old.position : fromDraft ? versionSpot.Value : BuildSpot();
            Quaternion turn = inPlace ? old.rotation : fromDraft ? versionTurn : BuildTurn();
            versionSpot = null;
            var replacing = inPlace ? Versions.FirstOrDefault(v => v.root == old) : null;
            // F81: the version on the stand goes into Maker's notebook (already on its way when Maker carved).
            if (asVersion && VersionsIntoNotebook && old != null && old.gameObject.activeSelf) ClearPlateForVersion(old);
            if (asVersion && !VersionsIntoNotebook && projectPlate != null && !MoveAsideFromPlate(old))
            {
                Design = endingLastDesign?.Copy() ?? archiveDesign?.Copy() ?? Design;
                Current = Step.Done;
                Say(Crew.Maker, "Make a little space next to the plate first.");
                yield break;
            }
            if (asVersion) ArchiveProduct(projectPlate == null && !fromDraft);
            else if (old != null)
            {
                if (held == old) { holdHand = null; mouseHolding = false; held = null; heldBody = null; }
                rails.Remove(old);
                Destroy(old.gameObject);
                Product = null;
            }
            if (!inPlace && pendingStand == null && projectPlate != null) pendingStand = projectPlate;
            productFinished = false;
            // F33: a Meshy model was already shaped and painted in front of the person (MakerSession.Sculpt.cs):
            // the made thing takes the place of that view at once, with no second build.
            bool revealed = revealedBySculpt;
            revealedBySculpt = false;
            if (!revealed)
            {
                Caption(Crew.Maker, "Building!", 1.6f);
                working.Add(Crew.Maker);
                if (!BodyWork(Crew.Maker, "Work_Loop")) Cue(Crew.Maker, "thinking");
            }
            HideTargetTag();
            HideTraceLines();
            var mesh = CoasterMesh.Build(Design);
            Paint(mesh, Design.Tint(new Color(.95f, .85f, .62f)));
            Give(mesh, at);
            if (replacedId != null) savedProductIds[Product] = replacedId;
            Product.rotation = turn;
            AfterSolid(Product, Design);
            ApplyMarks(Product, paint);
            if (revealed) SculptHandOff();
            else
            {
                buildFrom = Time.time;
                for (int i = 0; i < 6; i++)
                {
                    ToySounds.Play(ToySounds.Cue.Press, at, .55f, 1.2f + (i % 2) * .25f);
                    ToyFx.Puff(at + Vector3.up * .01f, .006f, 2);
                    yield return new WaitForSeconds(.27f);
                }
                buildFrom = -1f;
            }
            if (Product == null) yield break;
            Product.localScale = Vector3.one;
            if (!revealed)
            {
                working.Remove(Crew.Maker);
                if (!BodyDone(Crew.Maker)) Cue(Crew.Maker, "encourage");
                ToyFx.Confetti(at + Vector3.up * .04f, 16);
                ToySounds.Play(ToySounds.Cue.Chime, at, .8f, 1.15f);
            }
            Current = Step.Done;
            PrintMinutes = EstimateMinutes(mesh);
            PrintWords(out string printMaterial, out float minutes);
            Note("made", "Made: " + Design.name, Design.Summary + ". " + printMaterial + ", about " + minutes.ToString("0") + " min to print.");
            Talk.Add((inPlace ? "Maker rebuilt it in place: " : "Maker built it: ") + Design.Summary);
            if (replacing != null) { replacing.root = Product; replacing.design = Design.Copy(); replacing.tag = VersionTag(Product, "v" + replacing.number); }
            if (!inPlace) yield return SettleOnStand(); // F52: onto the stand chosen for it (a hop if made beside it)
            if (Product == null) yield break;
            productFinished = true;
            endingLastProduct = Product;
            endingLastDesign = Design.Copy();
            FinishActivity();
            Remember();
            AfterMake();
            RecordShape();
            MoveCubeAside(Product);
            Cue(Crew.Maker, "idle");
        }

        // Where a new made thing goes: on the real object's spot (F30: on its own spot, turned its way).
        private Vector3 BuildSpot() => Design != null && Design.IsFitShape ? MugSpot + Vector3.up * (Design.kind == "clip" ? 0f : .0005f) : MugSpot;
        private Quaternion BuildTurn() => Design != null && Design.IsFitShape ? MugTurn : Quaternion.identity;

        private void PrintWords(out string printMaterial, out float minutes)
        {
            printMaterial = string.IsNullOrWhiteSpace(Material) ? "PETG" : MakerRoles.Clean(Material).Split(':', ',')[0].Trim();
            minutes = Mathf.Max(5f, Mathf.Round(PrintMinutes / 5f) * 5f);
        }

        // Fixer's gap label beside the made thing for a while.
        private void ShowGapCard()
        {
            if (gapCard == null || Product == null) return;
            var renderer = Product.GetComponentInChildren<MeshRenderer>();
            var b = renderer != null ? renderer.bounds : new Bounds(Product.position, Vector3.one * .08f);
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - b.center, Vector3.up).normalized : Vector3.back;
            gapCard.position = new Vector3(b.center.x, b.max.y, b.center.z) + toUser * (Mathf.Max(b.extents.x, b.extents.z) + .02f) + Vector3.up * .025f;
            gapCard.rotation = SpeechBubbleDock.FacingAt(gapCard.position);
            gapCard.gameObject.SetActive(true);
            gapUntil = Time.time + 12f;
        }

        private void HideFitMarks()
        {
            if (gapCard != null) gapCard.gameObject.SetActive(false);
        }

        private void Give(Mesh mesh, Vector3 at)
        {
            if (Product != null) Destroy(Product.gameObject);
            ProductMesh = mesh;
            Product = Solid(mesh, "Made " + Design.name, at, out var body);
            ProductBody = body;
            Product.localScale = new Vector3(1f, .02f, 1f);
        }

        // A made thing with a convex collider and a (kinematic until let go) body. `design`: what it is
        // (default the current design): its parts for the brush and its look (F30: a model's own texture).
        private Transform Solid(Mesh mesh, string name, Vector3 at, out Rigidbody body, CoasterDesign design = null)
        {
            design ??= Design;
            owned.Add(mesh);
            var root = new GameObject(name).transform;
            root.SetParent(transform, false);
            var go = new GameObject("Mesh", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(root, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = MadeMaterial(design);
            // The brush paints it part by part (rim, floor, bottom, outside, inside; F30: per kind).
            PaintableThing.Make(go.GetComponent<MeshFilter>(), go.GetComponent<MeshRenderer>(), mesh, FitShapes.ClassifierOf(design), FitShapes.PartsOf(design), design != null && design.kind == "model");
            var collider = go.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.convex = true;
            body = root.gameObject.AddComponent<Rigidbody>();
            body.mass = .12f;
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            root.SetPositionAndRotation(at, Quaternion.identity);
            madeDesigns[root] = design; // F41: what each made thing is (a clip clicks onto edges)
            return root;
        }

        private static void Paint(Mesh mesh, Color tint)
        {
            var colors = new Color[mesh.vertexCount];
            for (int i = 0; i < colors.Length; i++) colors[i] = tint;
            mesh.colors = colors;
        }

        // Rough print time: the solid volume at ~45% fill, ~2.2 minutes per gram of PETG.
        private static float EstimateMinutes(Mesh mesh)
        {
            var v = mesh.vertices;
            var t = mesh.triangles;
            double volume = 0;
            for (int i = 0; i < t.Length; i += 3)
                volume += Vector3.Dot(v[t[i]], Vector3.Cross(v[t[i + 1]], v[t[i + 2]])) / 6.0;
            float cm3 = Mathf.Abs((float)volume) * 1e6f;
            return cm3 * 1.27f * .45f * 2.2f;
        }

        // Grab, move, let go: it falls and lands on whatever is below (the desk, the floor, via MRUK
        // colliders). Anything made can be picked up: this make, its versions, last time's make and the
        // older ones restored in the room. Up close the pinch holds it; from afar (the hand's ray on it) it
        // is carried at that distance along the ray.
        public Transform Held => held;
        private Transform held;
        private Rigidbody heldBody;
        private float heldFar = -1f;
        private readonly Dictionary<Transform, string> madeNames = new Dictionary<Transform, string>();

        // Everything made that is in the room now.
        public List<Transform> MadeThings()
        {
            var things = new List<Transform>();
            // (F94: the made one under the see-through one on the stand is not drawn and cannot be touched)
            void Add(Transform t) { if (t != null && t.gameObject.activeInHierarchy && !things.Contains(t) && !plateHiddenDraw.ContainsKey(t)) things.Add(t); }
            Add(Product);
            Add(Kept);
            Add(TestCube);
            if (Current == Step.Done || WaitingForPlacement) Add(Draft);
            Add(placementView);
            if (SizeBoxPhase == BoxPhase.Sizing || SizeBoxPhase == BoxPhase.Ready) Add(sizeBox); // F75: picked up and moved like a stand
            foreach (var s in Stands) Add(s); // F52: display stands are grabbed and moved like made things
            foreach (var v in Versions) Add(v.root);
            foreach (var t in RoomItems) Add(t);
            return things;
        }

        private Transform MadeNear(Vector3 point)
        {
            Transform best = null;
            float bestDistance = float.MaxValue;
            var gripped = StandGripAt(point); // F97: a pinch anywhere round the stand's plate takes the stand
            if (gripped != null) return gripped;
            var rider = StandRiderAt(point); // F102: unless the model on it is nearer the fingers (then the model alone)
            if (rider != null) return rider;
            foreach (var t in MadeThings())
            {
                var renderer = t.GetComponentInChildren<MeshRenderer>();
                if (renderer == null) continue;
                var bounds = renderer.bounds;
                bounds.Expand(.05f);
                if (!bounds.Contains(point)) continue;
                float d = Vector3.Distance(bounds.center, point);
                if (d < bestDistance) { bestDistance = d; best = t; }
            }
            return best;
        }

        public Transform MadeOnRay(Ray ray, out float distance)
        {
            distance = float.PositiveInfinity;
            Transform best = null;
            var things = MadeThings();
            if (things.Count == 0) return null;
            foreach (var hit in Physics.RaycastAll(ray, 4f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.distance >= distance) continue;
                var t = things.FirstOrDefault(m => hit.transform.IsChildOf(m));
                if (t == null) continue;
                distance = hit.distance;
                best = t;
            }
            return best;
        }

        private void Grab(Hand hand, Vector3 pinch, Transform thing = null, float far = -1f)
        {
            thing ??= Product;
            if (thing == null || (thing == Product && buildFrom >= 0f) || SolidHolds(thing)) return; // F59: not the clay being sculpted
            var body = thing.GetComponent<Rigidbody>();
            if (body == null) return;
            EnsureRoomScan(); // F38: the room scan is met too, when the room has one
            Touching = false;
            held = thing;
            heldBody = body;
            heldFar = far;
            holdHand = hand;
            // An interpolated body would put back its own pose over the hand's: none while held.
            body.interpolation = RigidbodyInterpolation.None;
            Quaternion turn = HandTurn(hand);
            holdOffset = Quaternion.Inverse(turn) * (thing.position - pinch);
            holdTurn = Quaternion.Inverse(turn) * thing.rotation;
            if (IsStand(thing)) { PickRiders(thing); standCarryTurn = Upright(thing.rotation); standCarryOffset = thing.position - pinch; standGrabY = thing.position.y; } // F52: what stands on it goes along; F97: its turn stays; F98
            body.isKinematic = true;
            heldPath.Clear();
            heldTimes.Clear();
            // F67: taken from afar it flies into the hand (then it is carried like a near grab); grab & release.
            heldFlyAt = far > 0f && HandRules && HandIO.GrabRelease && hand != null ? Time.time : -1f;
            heldFlyFrom = thing.position;
            if (hand != null) HandIO.BeginHold(hand);
            ToySounds.Play(ToySounds.Cue.Paper, thing.position, .5f, 1.2f);
        }

        public const float FlyToHand = .25f;
        private float heldFlyAt = -1f;
        private Vector3 heldFlyFrom;

        // Where a thing flying into the hand goes: its middle a little in front of the pinch.
        private Vector3 InHand(Vector3 pinch)
        {
            var r = held != null ? held.GetComponentInChildren<Renderer>() : null;
            Vector3 middleOffset = r != null ? held.position - r.bounds.center : Vector3.zero;
            Vector3 away = Head != null ? (pinch - Head.position).normalized : Vector3.forward;
            float half = r != null ? Mathf.Min(r.bounds.extents.magnitude, .12f) : .04f;
            return pinch + away * (half * .6f) + middleOffset;
        }

        private void Hold(Vector3 pinch, Quaternion turn)
        {
            if (held == null) { holdHand = null; mouseHolding = false; return; }
            Vector3 target;
            Quaternion rot;
            if (heldFlyAt >= 0f)
            {
                // F67: flying into the hand; on arrival it is held like a near grab (offset from the pinch, its turn).
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.time - heldFlyAt) / FlyToHand));
                target = Vector3.Lerp(heldFlyFrom, InHand(pinch), k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * .04f);
                rot = held.rotation;
                if (k >= 1f)
                {
                    heldFlyAt = -1f;
                    heldFar = -1f;
                    holdOffset = Quaternion.Inverse(turn) * (target - pinch);
                    holdTurn = Quaternion.Inverse(turn) * held.rotation;
                    standCarryOffset = target - pinch;
                }
            }
            else if (heldFar > 0f && holdHand != null && HandIO.Pointer(holdHand, out Pose pointer)) { target = pointer.position + pointer.forward * heldFar; rot = held.rotation; }
            else if (IsStand(held)) { target = pinch + standCarryOffset; rot = held.rotation; } // F98: a stand does not swing with the wrist
            else { target = pinch + turn * holdOffset; rot = turn * holdTurn; }
            if (IsStand(held)) target.y = Mathf.Max(target.y, StandLowest(target)); // F98: never down beside or under the desk
            // F41: a clip on a desk edge slides along it (or comes off when pulled away); F38: anything else
            // moves without going into the room or another made thing.
            bool stand = IsStand(held);
            if (held == sizeBox) rot = Upright(rot); // F75: the size box stays upright
            // F52: a stand stays upright and carries what stands on it. F97 (the user: "when I let it go it should not be
            // twisted, at 0 degrees"): it no longer turns with the wrist either; it keeps the turn it had when taken.
            if (stand) rot = standCarryTurn;
            if (!HoldOnEdge(target, rot)) MoveHeld(target, rot);
            if (stand) CarryRiders(held);
            heldPath.Add(held.position);
            heldTimes.Add(Time.time);
            while (heldTimes.Count > 6) { heldPath.RemoveAt(0); heldTimes.RemoveAt(0); }
        }

        private void LetGo()
        {
            if (holdHand != null) HandIO.EndHold(holdHand);
            heldFlyAt = -1f;
            holdHand = null;
            mouseHolding = false;
            var thing = held;
            var body = heldBody;
            held = null;
            heldBody = null;
            heldFar = -1f;
            if (thing == null || body == null) return;
            Touching = false;
            body.isKinematic = true;
            body.position = thing.position;
            body.rotation = thing.rotation;
            if (OnRail(thing))
            {
                // F30: a clip stays clamped where it was slid to.
                rails[thing] = (thing.position, rails[thing].along, thing.rotation);
                Talk.Add("Person slid it along the desk edge");
                StartCoroutine(RememberWhenSettled(thing));
                return;
            }
            // F41: a clip let go close to a desk edge clicks onto it.
            if (IsStand(thing)) { StandLetGo(thing); return; } // F52: settles onto the surface below
            if (thing == sizeBox) { BoxLetGo(); return; }      // F75: the size box too
            if (thing == Draft || thing == placementView) { Heard(); return; }
            if (TrySelectPlateRider(thing)) return;
            if (SnapOnEdgeAtRelease(thing)) { StartCoroutine(RememberWhenSettled(thing)); return; }
            if (DropOntoStand(thing)) { Heard(); return; } // F104: just above a stand it comes down onto it
            // F38: it stays exactly where it was let go (no gravity, no throw).
            Talk.Add("Person put it down there");
            ToySounds.Play(ToySounds.Cue.Press, thing.position, .3f, 1.45f);
            StartCoroutine(RememberWhenSettled(thing));
        }

        private IEnumerator RememberWhenSettled(Transform thing)
        {
            yield return new WaitForSeconds(1.5f);
            if (thing == null || held == thing) yield break;
            if (thing == Product) Remember();
            else if (madeNames.TryGetValue(thing, out var name)) RememberPlace(name, thing);
        }

        // A thing made before was moved: its entry keeps its place in the list, with the new spot.
        private void RememberPlace(string name, Transform thing)
        {
            var memory = LoadMemory();
            var item = SavedItem(memory, thing, name);
            if (item == null) return;
            var anchor = AnchorUnder(thing.position); // F38: by the anchor's id (names repeat)
            Vector3 local = anchor != null ? anchor.transform.InverseTransformPoint(thing.position) : thing.position;
            Quaternion rotation = anchor != null ? Quaternion.Inverse(anchor.transform.rotation) * thing.rotation : thing.rotation;
            item.anchor = anchor != null ? AnchorKey(anchor) : "";
            item.local = new[] { local.x, local.y, local.z };
            item.rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w };
            item.world = new[] { thing.position.x, thing.position.y, thing.position.z };
            item.turn = new[] { thing.rotation.x, thing.rotation.y, thing.rotation.z, thing.rotation.w }; // F91
            SaveStandPose(item, thing); // F52; F104: with the stand's pose under it
            try { File.WriteAllText(MemoryPath, JsonConvert.SerializeObject(memory, Formatting.Indented)); }
            catch (Exception exception) { Debug.LogWarning("[Windups Maker] memory: " + exception.Message); }
        }

        // ------------------------------------------------------------ Spark's memory

        [Serializable] private sealed class Memory { public List<Made> items = new List<Made>(); }
        [Serializable] private sealed class Made
        {
            public string id, name, anchor, fit, savedAt, notebookId;
            public CoasterDesign design;
            public float[] local, rotation, world;
            public float[] turn; // F91: its world turn (with `world`, where it comes back when its anchor is gone)
            public List<PaintableThing.Mark> paint;
            public float stand; // F52: the radius of the display stand it stands on (0: none)
            public float[] standAt, standTurn; // F104: that stand's place and turn relative to the thing (null: older saves)
        }

        private void Remember()
        {
            if (Product == null || Design == null) return;
            QueueNotebookSnapshot(Product);
            var memory = LoadMemory();
            var previous = SavedItem(memory, Product, Design.name);
            if (previous != null) memory.items.Remove(previous);
            string itemId = SavedProductId(Product);
            var anchor = AnchorUnder(Product.position); // F38: by the anchor's id (names repeat)
            Vector3 local = anchor != null ? anchor.transform.InverseTransformPoint(Product.position) : Product.position;
            Quaternion rotation = anchor != null ? Quaternion.Inverse(anchor.transform.rotation) * Product.rotation : Product.rotation;
            memory.items.Add(new Made
            {
                id = itemId, name = Design.name, notebookId = project, design = Design, anchor = anchor != null ? AnchorKey(anchor) : "",
                local = new[] { local.x, local.y, local.z }, rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                world = new[] { Product.position.x, Product.position.y, Product.position.z }, fit = "", savedAt = DateTime.UtcNow.ToString("o"),
                turn = new[] { Product.rotation.x, Product.rotation.y, Product.rotation.z, Product.rotation.w }, // F91
                paint = Product.GetComponentInChildren<PaintableThing>()?.SavedMarks()
            });
            SaveStandPose(memory.items[memory.items.Count - 1], Product); // F52; F104: with the stand's pose under it
            try { File.WriteAllText(MemoryPath, JsonConvert.SerializeObject(memory, Formatting.Indented)); }
            catch (Exception exception) { Debug.LogWarning("[Windups Maker] memory: " + exception.Message); }
        }

        private static Memory LoadMemory()
        {
            try { if (File.Exists(MemoryPath)) return JsonConvert.DeserializeObject<Memory>(File.ReadAllText(MemoryPath)) ?? new Memory(); }
            catch (Exception exception) { Debug.LogWarning("[Windups Maker] memory: " + exception.Message); }
            return new Memory();
        }

        public static void ForgetAll() { try { if (File.Exists(MemoryPath)) File.Delete(MemoryPath); } catch { } ForgetNotes(); ForgetPalette(); ForgetMoodboard(); ForgetWall(); }

        // What was made last time comes back where it lives, and Spark asks how it went.
        // F91 (2026-11-15, Quest: nothing made before could be picked up on coming back): every saved make comes back. One
        // whose anchor is not there on the way in (MRUK still loading, the room re-scanned; in the Editor the sample
        // room's anchor ids change every Play) waits for the welcome and then comes back at the world spot and turn it was
        // saved at (RestorePlace); it is never skipped for good (that was the bug: the way in marked the restore done).
        private void Restore()
        {
            if (Kept != null && restoredMade.Count == 0) return; // made in this run: already here
            var all = FinalOnly(LoadMemory().items); // F81: only the final make of each project comes back
            var last = all.LastOrDefault();
            RestoreRoom(all.Where(item => item != last).ToList());
            RestorePins();
            if (last?.design == null || Kept != null || restoredMade.Contains(RestoreKey(last))) return;
            if (!RestorePlace(last, out Vector3 at, out Quaternion rot, out bool byWorld)) return; // the welcome tries again
            restoredMade.Add(RestoreKey(last));
            var design = last.design.Clamped();
            var mesh = CoasterMesh.Build(design);
            Paint(mesh, design.Tint(new Color(.95f, .85f, .62f)));
            Kept = Solid(mesh, "Kept " + design.name, at, out _, design);
            Kept.rotation = rot;
            if (byWorld) ClearOfRoom(Kept);
            AfterSolid(Kept, design);
            madeNames[Kept] = last.name;
            TrackRestoredWork(last, Kept);
            if (!string.IsNullOrEmpty(last.notebookId)) notebookOwners[Kept] = last.notebookId;
            ApplyMarks(Kept, last.paint);
            RestoreStand(Kept, last.stand, last.standAt, last.standTurn);
            Remembered = true;
            if (restoringAtLaunch) return;
            ToyFx.Puff(at + Vector3.up * .01f, .01f, 4);
            awaitingFit = string.IsNullOrEmpty(last.fit);
            if (Begun && awaitingFit) AskWith(Crew.Spark, $"Your {MakerRoles.Clean(last.name)} is right where you left it. Did it fit?", false, Rp("It fit"), Rp("Too tight"), Rp("Too loose"));
        }

        private bool FitAnswer(string p)
        {
            string verdict = p.Contains("too big") || p.Contains("loose") || p.Contains("wobbl") || p.Contains("헐렁") ? "loose"
                : p.Contains("too small") || p.Contains("tight") || p.Contains("didn't fit") || p.Contains("doesn't fit") || p.Contains("빡빡") || p.Contains("안 들어") ? "tight"
                : p.Contains("fit") || p.Contains("perfect") || p.Contains("great") || IsYes(p) || p.Contains("잘 맞") ? "good" : null;
            if (verdict == null) return false;
            awaitingFit = false;
            var memory = LoadMemory();
            if (memory.items.Count > 0) { memory.items[memory.items.Count - 1].fit = verdict; try { File.WriteAllText(MemoryPath, JsonConvert.SerializeObject(memory, Formatting.Indented)); } catch { } }
            if (verdict == "good") Say(Crew.Spark, "Yay! Noted.");
            else
            {
                fitBias = verdict == "tight" ? .75f : -.5f;
                Say(Crew.Fixer, verdict == "tight" ? "Noted. The next one gets a roomier gap." : "Noted. The next one fits snugger.");
            }
            Talk.Add("Last make fit: " + verdict);
            Note("feedback", "How it fit", verdict == "good" ? "It fit well." : verdict == "tight" ? "Too tight: next time a roomier gap." : "Too loose: next time snugger.");
            if (Current == Step.Suggesting && Ideas.Count == 0 && string.IsNullOrEmpty(Need)) AskWith(Crew.Maker, "So, what shall we make today?", false, IdeaReplies);
            return true;
        }

        // ------------------------------------------------------------ 5. print

        public void Print()
        {
            if (Product == null || Design == null) { Say(Crew.Maker, "Let's make it first."); return; }
            // F57: a Meshy model is rebuilt into a closed solid off the main thread: STL + 3MF (colours) + GLB (MakerSession.Solid.cs)
            if (Design.kind == "model") { if (!Printing) StartCoroutine(PrintSolid()); else Caption(Crew.Fixer, "Still writing the files…", 2f); return; }
            // F41: a clip is printed lying on its side (its bend along the layers); F42: Fixer's check first.
            var printMesh = PrintOriented(ProductMesh, Design);
            byte[] stl = CoasterMesh.Stl(printMesh, Product.localScale.x, "Windups " + Design.name);
            if (printMesh != ProductMesh) Destroy(printMesh);
            SayPrintCheck(true);
            string name = Regex.Replace(Design.name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-') + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".stl";
            string folder = Path.Combine(Application.persistentDataPath, "prints");
            try
            {
                Directory.CreateDirectory(folder);
                LastPrint = Path.Combine(folder, name);
                File.WriteAllBytes(LastPrint, stl);
                File.WriteAllText(Path.ChangeExtension(LastPrint, ".json"), JsonConvert.SerializeObject(new { Need, Style, look = Look?.label, Shape, mugBaseMm = Measured, design = Design, material = Material, minutes = PrintMinutes }, Formatting.Indented));
            }
            catch (Exception exception) { LastError = exception.Message; }
            // F30: the same thing as a 3MF with a colour per part, as painted (for colour printers).
            byte[] colour = null;
            string colourName = Path.ChangeExtension(name, ".3mf");
            try
            {
                colour = ColourPrint();
                if (colour != null && LastPrint != null) { LastColourPrint = Path.ChangeExtension(LastPrint, ".3mf"); File.WriteAllBytes(LastColourPrint, colour); }
            }
            catch (Exception exception) { Debug.LogWarning("[Windups Maker] 3MF: " + exception.Message); colour = null; }
            ToyFx.Confetti(Product.position + Vector3.up * .05f, 16);
            Talk.Add("Person printed it");
            StartCoroutine(Send(stl, name, colour, colourName));
            StartCoroutine(GlbBeside(LastPrint)); // F57: and a 3D file for 3D programs
        }

        public string LastColourPrint { get; private set; }

        // The made thing's parts and their colours: painted ones as painted, the rest in the design's colour
        // (a model's own texture: its average colour).
        private byte[] ColourPrint()
        {
            var thing = Product != null ? Product.GetComponentInChildren<PaintableThing>() : null;
            if (thing == null || thing.Mesh == null || thing.Parts == null) return null;
            var base_ = Design.Tint(new Color(.95f, .85f, .62f));
            var colours = new List<Color>();
            for (int i = 0; i < thing.Parts.Length; i++)
                colours.Add(thing.Marks.TryGetValue(i, out var mark) && mark != null && ColorUtility.TryParseHtmlString(mark.hex ?? "", out var painted) ? painted : base_);
            var parts = PrintOriented(thing.Mesh, Design); // F41: the same way up as the STL
            try { return FitShapes.ThreeMf(parts, Product.localScale.x, thing.Parts, colours, Design.name); }
            finally { if (parts != thing.Mesh) Destroy(parts); }
        }

        private IEnumerator Send(byte[] stl, string name, byte[] colour = null, string colourName = null)
        {
            LastPrintSent = false;
            string pc = WindupsDirectCredentials.PcUrl;
            if (!string.IsNullOrEmpty(pc))
            {
                using (var http = new UnityWebRequest(pc + "/api/print?name=" + Uri.EscapeDataString(name), "POST"))
                {
                    http.uploadHandler = new UploadHandlerRaw(stl);
                    http.downloadHandler = new DownloadHandlerBuffer();
                    http.SetRequestHeader("Content-Type", "model/stl");
                    http.timeout = 8;
                    yield return http.SendWebRequest();
                    LastPrintSent = http.result == UnityWebRequest.Result.Success;
                }
                if (LastPrintSent && colour != null)
                    using (var http = new UnityWebRequest(pc + "/api/print?name=" + Uri.EscapeDataString(colourName), "POST"))
                    {
                        http.uploadHandler = new UploadHandlerRaw(colour);
                        http.downloadHandler = new DownloadHandlerBuffer();
                        http.SetRequestHeader("Content-Type", "model/3mf");
                        http.timeout = 8;
                        yield return http.SendWebRequest();
                    }
            }
            Say(Crew.Maker, LastPrintSent ? "Sent to your PC, in the prints folder." : "Saved on the headset. Your PC wasn't reachable.");
        }

        // ------------------------------------------------------------ Spark's eyes and legs

        private IEnumerator SparkLooks(string question, Action<List<RoomEyes.Found>> done)
        {
            looking = true;
            working.Add(Crew.Spark);
            Cue(Crew.Spark, "thinking");
            List<RoomEyes.Found> found = null;
            ToySounds.Play(ToySounds.Cue.Press, Head != null ? Head.position + Head.forward * .3f : transform.position, .4f, 1.8f); // click
            yield return Eyes.Look(question, list => found = list);
            working.Remove(Crew.Spark);
            Cue(Crew.Spark, "idle");
            looking = false;
            ShowSeen(found);
            done(found ?? new List<RoomEyes.Found>());
        }

        // Spark trots across the desk to look closely, then goes back to its seat.
        private IEnumerator Wander(Vector3 target)
        {
            if (!cast.TryGetValue(Crew.Spark, out var spark) || spark == null) yield break;
            var pet = spark.movement;
            Vector3 home = pet.transform.position;
            Vector3 away = Vector3.ProjectOnPlane(home - target, Vector3.up);
            Vector3 near = target + (away.sqrMagnitude > .0001f ? away.normalized : Vector3.forward) * .08f;
            SparkWalking = true;
            focus.position = target;
            spark.lookTarget = focus;
            pet.UnlockMeetingSeat();
            yield return null;
            // As close as the walkable surface allows (furniture can split it: then the nearest reachable point).
            var room = arrival.handoff.meeting.environment;
            Vector3 goal = room.TrySameLevelNavPoint(near, out var snapped) ? snapped : near;
            var path = new UnityEngine.AI.NavMeshPath();
            if (UnityEngine.AI.NavMesh.CalculatePath(pet.transform.position, goal, room.Query, path) && path.status == UnityEngine.AI.NavMeshPathStatus.PathPartial && path.corners.Length > 1)
                goal = path.corners[path.corners.Length - 1];
            bool going = Vector3.Distance(goal, home) > .1f && pet.GoTo(goal, 2.5f);
            Debug.Log($"[Windups Maker] Spark wanders: home {home:F3} goal {goal:F3} path {path.status} going {going} state {pet.State}");
            if (going)
            {
                float until = Time.time + 6f;
                while (Time.time < until && pet.State == RoomCapsule.CapsulePetController.MotionState.Walking && Vector3.Distance(pet.transform.position, goal) > .05f)
                {
                    SparkReach = Mathf.Max(SparkReach, Vector3.Distance(pet.transform.position, home));
                    yield return null;
                }
                SparkReach = Mathf.Max(SparkReach, Vector3.Distance(pet.transform.position, home));
                ToySounds.Play(ToySounds.Cue.Pop, target, .4f, 1.5f);
                var peeker = WindupBody.Of(spark);
                if (peeker != null) peeker.Play("Peek"); else spark.SetCue("encourage");
                yield return new WaitForSeconds(.9f);
                spark.SetCue("idle");
                home = ClearSeat(Crew.Spark, home); // F51
                going = pet.GoTo(home, 3f);
                until = Time.time + 6f;
                while (going && Time.time < until && pet.State == RoomCapsule.CapsulePetController.MotionState.Walking && Vector3.Distance(pet.transform.position, home) > .05f) yield return null;
                for (int i = 0; i < 60 && !pet.MeetingSeatLocked; i++) { pet.LockMeetingSeat(); yield return null; }
            }
            else
            {
                // Can't get there: lean over and peek from the seat.
                for (int i = 0; i < 30 && !pet.MeetingSeatLocked; i++) { pet.LockMeetingSeat(); yield return null; }
                Vector3 peek = Vector3.ProjectOnPlane(target - home, Vector3.up).normalized * .06f;
                spark.SetCue("encourage");
                for (float t = 0f; t < 1f; t += Time.deltaTime / .35f) { pet.transform.position = home + peek * Mathf.SmoothStep(0f, 1f, t); yield return null; }
                SparkReach = Mathf.Max(SparkReach, peek.magnitude);
                ToySounds.Play(ToySounds.Cue.Pop, target, .4f, 1.5f);
                yield return new WaitForSeconds(.7f);
                for (float t = 0f; t < 1f; t += Time.deltaTime / .35f) { pet.transform.position = home + peek * Mathf.SmoothStep(1f, 0f, t); yield return null; }
                pet.transform.position = home;
                spark.SetCue("idle");
            }
            SparkWalking = false;
        }

        // "What do you see?": name tags on the objects in view for a few seconds.
        private IEnumerator NameThings()
        {
            if (Eyes == null || Eyes.Looking) yield break;
            Ack(Crew.Spark, "Let me look!");
            List<RoomEyes.Found> found = null;
            yield return SparkLooks("", list => found = list);
            if (found == null || found.Count == 0) { Say(Crew.Spark, Eyes.LastError ?? "I don't see much here."); yield break; }
            Say(Crew.Spark, "I see " + Join(found.Select(f => MakerRoles.Clean(f.seen.name)).ToList()) + ".");
        }

        private static string Join(List<string> names) =>
            names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
        private static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // A name tag over a real object, with its colours as little chips.
        private Transform Tag(RoomEyes.Found found, bool size, string label = null)
        {
            var tag = PaperTag("Seen " + found.seen.name, .09f, .026f, Color.white, out var text, 11f); // F77: a paper tag, not a pill
            text.text = (label ?? MakerRoles.Clean(found.seen.name)) + (size && found.width > 0f ? "  ~" + Mathf.Round(found.width * 1000f) + " mm" : "");
            var colors = found.seen.colors ?? new string[0];
            for (int i = 0; i < colors.Length && i < 3; i++)
            {
                if (!ColorUtility.TryParseHtmlString(colors[i], out var c)) continue;
                var chip = ToyMeshes.RoundedSlab(.014f, .014f, .007f, .003f, c, Color.Lerp(c, Color.black, .2f), c, "ColourChip");
                owned.Add(chip);
                var go = new GameObject("Colour", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(tag, false);
                go.transform.localPosition = new Vector3((i - (Mathf.Min(colors.Length, 3) - 1) * .5f) * .018f, -.024f, 0f);
                go.GetComponent<MeshFilter>().sharedMesh = chip;
                go.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            // The tail under the card (and its colour chips) points at the thing.
            AddTail(tag, .026f, Color.white, colors.Length > 0 ? .02f : 0f);
            tag.position = found.top + Vector3.up * TagGap;
            Tags.Add(tag);
            ToyFx.Puff(found.top, .01f, 3);
            ToySounds.Play(ToySounds.Cue.Pop, found.top, .4f, 1.3f);
            return tag;
        }

        private void ClearTags()
        {
            foreach (var t in Tags) if (t != null) Destroy(t.gameObject);
            Tags.Clear();
        }

        // ------------------------------------------------------------ AI, pictures and waiting gags

        private IEnumerator Research()
        {
            string json = null;
            yield return Ask(Crew.Fixer, "material", new { need = Need, talk = Talk.ToArray() }, r => json = r, false);
            if (json == null) yield break;
            Material = JsonConvert.DeserializeObject<MaterialReply>(json).material ?? "";
        }

        private IEnumerator Ask(Crew crew, string job, object input, Action<string> done, bool show = true, IList<byte[]> images = null, bool reportErrors = true)
        {
            working.Add(crew);
            if (show) Cue(crew, "thinking");
            LastError = null;
            NoteAsk(job, input); // F63: what was asked (maker_ai_log.jsonl, checks)
            string json = null, error = null;
            if (Scripted)
            {
                yield return new WaitForSeconds(.4f);
                json = MakerRoles.Scripted(job, Measured, Eyes?.LastFound.FirstOrDefault(f => f.seen.focus)?.seen.name ?? "");
            }
            else if (!WindupsDirectCredentials.TryGetKey(out string key)) error = "AI access is not configured in this test build.";
            else
            {
                using (var http = new UnityWebRequest(WindupsDirectMeeting.Endpoint, "POST"))
                {
                    http.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(MakerRoles.CreateRequest(job, input, images)));
                    http.downloadHandler = new DownloadHandlerBuffer();
                    http.SetRequestHeader("Content-Type", "application/json");
                    http.SetRequestHeader("Authorization", "Bearer " + key);
                    http.timeout = 90;
                    yield return http.SendWebRequest();
                    if (this == null) yield break;
                    if (http.result != UnityWebRequest.Result.Success)
                        error = http.responseCode == 429 ? "AI limit reached. Try again soon." : "Could not connect. Check the internet.";
                    else
                        try { json = MakerRoles.ReadOutput(http.downloadHandler.text); }
                        catch (Exception exception) { error = "I lost my thought. Say it again."; Debug.LogWarning("[Windups Maker] " + exception.Message); }
                }
            }
            working.Remove(crew);
            if (show) Cue(crew, "idle");
            if (error != null)
            {
                LastError = error;
                if (reportErrors) Caption(crew, error, 5f);
                Debug.LogWarning("[Windups Maker] " + job + ": " + error);
                yield break;
            }
            Debug.Log("[Windups Maker] " + job + ": " + json);
            LogAsk(job, json, images?.Count ?? 0); // F63
            done(json);
        }

        // One picture from the image model (a placeholder in the scripted Editor preview). The card stays
        // hidden; Maker draws in its book until all of them are done.
        private IEnumerator Draw(Picture picture, string prompt, int index)
        {
            byte[] jpeg = null;
            if (Scripted)
            {
                yield return new WaitForSeconds(.5f + index * .15f);
                var tex = new Texture2D(64, 64);
                Color a = new[] { new Color(.55f, .38f, .25f), new Color(.56f, .73f, .59f), new Color(.95f, .9f, .8f) }[index % 3];
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                    tex.SetPixel(x, y, new Vector2(x - 32, y - 32).magnitude < 24 ? a : Color.Lerp(a, Color.white, .7f));
                tex.Apply();
                jpeg = tex.EncodeToJPG();
                Destroy(tex);
            }
            else if (WindupsDirectCredentials.TryGetKey(out string key))
                using (var http = PictureRequestFor(prompt, index)) // F49: per round (MakerSession.FormFirst.cs); F63: and per picture
                {
                    http.SetRequestHeader("Authorization", "Bearer " + key);
                    http.timeout = 120;
                    yield return http.SendWebRequest();
                    if (http.result == UnityWebRequest.Result.Success)
                        try { jpeg = Convert.FromBase64String((string)JObject.Parse(http.downloadHandler.text)["data"][0]["b64_json"]); }
                        catch (Exception exception) { Debug.LogWarning("[Windups Maker] picture: " + exception.Message); }
                    else Debug.LogWarning("[Windups Maker] picture failed: " + http.responseCode + " " + http.downloadHandler.text);
                }
            picture.Done = true;
            if (picture.root == null) yield break;
            if (jpeg == null) { LastError = "picture failed"; yield break; }
            var texture = new Texture2D(2, 2);
            texture.LoadImage(jpeg);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = 4;
            owned.Add(texture);
            picture.jpeg = jpeg;
            Note("look", "Design study", "", jpeg);
            picture.texture = texture;
            picture.image.texture = texture;
            picture.image.color = Color.white;
        }

        // The real object's photo goes along: the picture takes its colours and surface texture.
        // F33: also a photo of a real object the person showed for the shape (put in the show-me circle).
        private static UnityWebRequest ReferenceRequest(string prompt, byte[] reference, byte[] drawing = null, byte[] shapePhoto = null)
        {
            var order = new List<string>();
            if (reference != null) order.Add("colour");
            if (drawing != null) order.Add("drawing");
            if (shapePhoto != null) order.Add("shape");
            string Nth(string what) { int i = order.IndexOf(what); return order.Count == 1 ? "the" : new[] { "the first", "the second", "the third" }[i]; }
            string guide = (reference != null ? " Use the colours and the surface texture of " + Nth("colour") + " image (a reference photo)." : "") +
                           (drawing != null ? " Give the object the shape of the person's line drawing (" + Nth("drawing") + " image)." : "") +
                           (shapePhoto != null ? " Give the object the overall shape and proportions of the real object in " + Nth("shape") + " image (a photo); do not copy its colours unless asked." : "");
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("model", MakerRoles.ImageModel),
                new MultipartFormDataSection("prompt", (prompt ?? "").Trim() + guide + MakerRoles.ImageStyle),
                new MultipartFormDataSection("size", "1024x1024"),
                new MultipartFormDataSection("quality", MakerRoles.ImageQuality),
                new MultipartFormDataSection("output_format", "jpeg"),
                new MultipartFormDataSection("output_compression", "92"),
            };
            if (reference != null) form.Add(new MultipartFormFileSection("image[]", reference, "reference.jpg", "image/jpeg"));
            if (drawing != null) form.Add(new MultipartFormFileSection("image[]", drawing, "drawing.jpg", "image/jpeg"));
            if (shapePhoto != null) form.Add(new MultipartFormFileSection("image[]", shapePhoto, "shape.jpg", "image/jpeg"));
            var http = UnityWebRequest.Post("https://api.openai.com/v1/images/edits", form);
            http.downloadHandler = new DownloadHandlerBuffer();
            return http;
        }

        // Maker draws the looks in its spiral sketchbook while the pictures are made: the book lies tilted
        // in front of it (facing up and toward the person, who looks down on the desk), and a pencil whose
        // top leans toward Maker's hand traces a real hand-drawn design sketch of each look (outline,
        // inner rim, a pattern, a size line). After a page: a short look, then the page flips over the
        // spiral; every third page is torn off, crumpled and tossed. Nothing moves inside the picture cards.
        public const float SketchSpeed = .05f; // metres of line per second
        private const float PadW = .056f, PadH = .042f;

        private IEnumerator Sketch()
        {
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null) yield break;
            yield return PrepareSketchSpace(maker);
            if (!sketchSpaceReady || (Current != Step.Looks && !drawingRevision && !sketchOnce)) yield break;
            Sketching = true;
            sketchWanted = true;
            tossing = false; // a toss cut short earlier must not keep the pencil away
            var drawer = WindupBody.Of(maker);
            drawer?.StartWork("Creativity_Make_Loop");
            // Creativity_Make_Loop bends far forward (8.8 deg lean, face 16 deg down): drawing in its lap it
            // stays nearly upright and only glances down at the page (WindupBody clears this with the work).
            drawer?.LimitPosture(SketchLean, SketchHeadDown);
            if (drawer == null) maker.SetCue("speak");
            PlacePad(maker, 1f);
            sketchPad.gameObject.SetActive(true);
            pencil.gameObject.SetActive(true);
            drawHand = 0;
            pencilTip = new Vector2(.018f, -.012f);
            pencilLift = .012f;
            PlacePencil(maker);
            ToyFx.Puff(sketchPad.position, .008f, 3);
            ToySounds.Play(ToySounds.Cue.Paper, sketchPad.position, .4f, 1.3f);
            int page = 0;
            float until = Time.time + 180f;
            bool Pending() => (sketchWanted && (Current == Step.Looks || drawingRevision) || sketchOnce) && Time.time < until;
            while (Pending())
            {
                var sheet = NewPage();
                var ink = sheet.Find("Ink").GetComponent<MeshFilter>().sharedMesh;
                var lines = new InkLines();
                foreach (var stroke in PagePlan(page))
                {
                    if (!Pending()) break;
                    // Pencil up, over to where the line starts.
                    Vector2 from = pencilTip;
                    for (float t = 0f; t < 1f && Pending(); t += Time.deltaTime / .16f)
                    {
                        pencilTip = Vector2.Lerp(from, stroke.points[0], Mathf.SmoothStep(0f, 1f, t));
                        pencilLift = .004f + Mathf.Sin(t * Mathf.PI) * .004f;
                        PlacePencil(maker);
                        yield return null;
                    }
                    pencilLift = 0f;
                    lines.Start(stroke.points[0], stroke.width);
                    float length = stroke.Length, done = 0f;
                    int next = 1;
                    Vector2 at = stroke.points[0];
                    if (length > .01f) ToySounds.Play(ToySounds.Cue.Paper, sketchPad.position, .1f, 2.6f);
                    while (next < stroke.points.Count && Pending())
                    {
                        // Slower into and out of each line, like a hand.
                        float k = length > 0f ? done / length : 1f;
                        float step = SketchSpeed * (Scripted ? 3f : 1f) * (.45f + .55f * Mathf.Sin(Mathf.Clamp01(k) * Mathf.PI)) * Time.deltaTime;
                        while (step > 0f && next < stroke.points.Count)
                        {
                            float left = Vector2.Distance(at, stroke.points[next]);
                            if (left <= step) { step -= left; done += left; at = stroke.points[next++]; lines.To(at); }
                            else { at = Vector2.MoveTowards(at, stroke.points[next], step); done += step; step = 0f; lines.To(at); }
                        }
                        pencilTip = at;
                        lines.Apply(ink);
                        PlacePencil(maker);
                        yield return null;
                    }
                    lines.Apply(ink);
                }
                if (!Pending()) break;
                // A look at the page: pencil up, two light taps.
                Vector2 rest = pencilTip;
                for (float t = 0f; t < 1f && Pending(); t += Time.deltaTime / (Scripted ? .3f : .9f))
                {
                    pencilLift = .006f + Mathf.Abs(Mathf.Sin(t * Mathf.PI * 2f)) * .006f;
                    PlacePencil(maker);
                    yield return null;
                }
                // Between pages Maker glances up at the person (its face shows from above, then back to the page).
                if (drawer != null && Head != null) drawer.LookAtPoint(Head.position, .7f);
                page++;
                sketchOnce = false; // a clean-up of the person's drawing is one page
                if (!Pending()) break;
                if (page % 3 == 0) { yield return TossDraft(maker, sheet); continue; }
                // Flip the page over the spiral.
                ToySounds.Play(ToySounds.Cue.Paper, sketchPad.position, .35f, 1.5f);
                var hinge = sheet.parent;
                for (float t = 0f; t < 1f; t += Time.deltaTime / .38f)
                {
                    hinge.localRotation = Quaternion.Euler(-175f * Mathf.SmoothStep(0f, 1f, t), 0f, 0f);
                    pencilLift = .014f;
                    PlacePencil(maker);
                    yield return null;
                }
                pages.Remove(hinge);
                Destroy(hinge.gameObject);
            }
            sketchWanted = false;
            if (drawer != null) drawer.StopWork(true); else maker.SetCue("encourage");
            ToyFx.Puff(sketchPad.position, .008f, 3);
            ClearDrafts(); // the pile of tossed pages goes as the pictures come out
            ClearPages();
            ReleaseHands();
            sketchPad.gameObject.SetActive(false);
            pencil.gameObject.SetActive(false);
            Sketching = false;
            yield return new WaitForSeconds(.8f);
            if (maker.Cue == "encourage") maker.SetCue("idle");
        }

        // Maker holds the book against its chest, below the face, the page tipped toward the person's eyes
        // (the top of the drawing away from them); one mitten draws, the other holds the book's edge, and
        // neither leaves the body (HandReach). PadSide/PadForward/PadUp/PadFaceUp place it.
        private Vector3 SketchSide(WindupCharacter maker, Vector3 toUser)
        {
            Vector3 side = Vector3.Cross(Vector3.up, toUser);
            Vector3 at = maker.transform.position;
            var others = cast.Where(c => c.Key != Crew.Maker && c.Value != null).Select(c => c.Value.transform.position).ToList();
            if (others.Count > 0 && Vector3.Dot(others.Aggregate(Vector3.zero, (a, b) => a + b) / others.Count - at, side) > 0f) side = -side;
            return side;
        }

        private void PlacePad(WindupCharacter maker, float blend)
        {
            Vector3 at = maker.transform.position;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - at, Vector3.up) : Vector3.back;
            toUser = toUser.sqrMagnitude > .0001f ? toUser.normalized : Vector3.back;
            Vector3 side = SketchSide(maker, toUser);
            Vector3 pos = at + side * PadSide + toUser * PadForward + Vector3.up * PadUp;
            Vector3 normal = (Vector3.up * PadFaceUp + toUser).normalized; // the drawing side
            Quaternion rot = Quaternion.LookRotation(-normal, -toUser);
            pos = ClearOfHead(maker, pos, rot, toUser);
            sketchPad.SetPositionAndRotation(Vector3.Lerp(sketchPad.position, pos, blend), Quaternion.Slerp(sketchPad.rotation, rot, blend));
            if (!tossing) PlacePencil(maker); // while a page is torn off and tossed, TossDraft has the mittens
        }

        // The book never goes into Maker's (big) head: if any part of the page comes within HeadMargin of it,
        // the book moves out toward the person, a few millimetres at a time.
        public const float SketchLean = 4f, SketchHeadDown = 12f, HeadMargin = 1.18f; // degrees, degrees, ellipsoid radii
        public int PadNudges { get; private set; }
        private Vector3 ClearOfHead(WindupCharacter maker, Vector3 pos, Quaternion rot, Vector3 toUser)
        {
            var body = WindupBody.Of(maker);
            if (body == null) return pos;
            for (int step = 0; step < 10 && PageNearHead(body, pos, rot) < HeadMargin; step++)
            {
                pos += toUser * .004f;
                PadNudges++;
            }
            return pos;
        }

        // The smallest head depth (1 = on the head's surface) over the page: corners, edge middles, centre.
        public static float PageNearHead(WindupBody body, Vector3 pos, Quaternion rot)
        {
            float least = 9f;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                least = Mathf.Min(least, body.HeadDepth(pos + rot * new Vector3(x * PadW * .5f, y * PadH * .5f, 0f)));
            return least;
        }

        // The pencil's point is on the page (or lifted off it); it stands up off the page leaning toward
        // Maker, and the mitten on the book's side holds it a little way up (the other hand stays free).
        private void PlacePencil(WindupCharacter maker)
        {
            if (pencil == null || sketchPad == null) return;
            Vector3 tip = sketchPad.TransformPoint(new Vector3(pencilTip.x, pencilTip.y, -.0012f - pencilLift));
            Vector3 at = maker.transform.position;
            var body = WindupBody.Of(maker);
            if (body == null) return;
            var right = body.RigHand(true);
            var left = body.RigHand(false);
            if (drawHand == 0)
            {
                // (2026-11-13, the user: "Maker's drawing pose would be much better with the hands swapped") It used to
                // draw with whichever hand was nearer the book (its left, as it turned out); now the right hand draws
                // and the left holds the book. The pencil slants out toward the drawing hand's side.
                bool useRight = DrawRightHanded ? right != null : left == null;
                drawHand = useRight ? 1 : -1;
                var h = useRight ? right : left;
                drawSide = h != null && sketchPad.InverseTransformPoint(h.position).x > 0f ? 1f : -1f;
                penHand = tip + sketchPad.TransformDirection(new Vector3(drawSide * .62f, .28f, -.73f).normalized) * .036f;
            }
            // Like writing: the mitten holds the pencil near its top, off to its own side of the page, and
            // follows the point a little behind (so the pencil tilts like a wrist); it stays by the body.
            Vector3 want = tip + sketchPad.TransformDirection(new Vector3(drawSide * .62f, .28f, -.73f).normalized) * .036f;
            Vector3 flat = Vector3.ProjectOnPlane(want - at, Vector3.up);
            float up = Mathf.Max(Vector3.Dot(want - at, Vector3.up), .03f);
            if (flat.magnitude > HandReach) flat = flat.normalized * HandReach;
            want = at + flat + Vector3.up * up;
            penHand = Vector3.Lerp(penHand, want, 1f - Mathf.Exp(-Time.deltaTime * 10f));
            Vector3 lean = penHand - tip;
            lean = lean.sqrMagnitude > 1e-8f ? lean.normalized : sketchPad.TransformDirection(Vector3.back);
            Vector3 inPad = sketchPad.InverseTransformDirection(lean);
            if (inPad.z > -.3f) { inPad.z = -.3f; lean = sketchPad.TransformDirection(inPad.normalized); }
            pencil.SetPositionAndRotation(tip, Quaternion.LookRotation(lean, Vector3.up));
            // (2026-11-13) The mitten keeps its own rest turn, only yawed toward the pencil: LookRotation(toward the pencil)
            // turned the mitten round ("the hands look twisted while drawing").
            var grip = body.NaturalGrip(drawHand > 0, pencil.position, 55f);
            body.Hold(drawHand > 0, new Pose(tip + lean * Mathf.Min(.038f, Vector3.Distance(tip, penHand)), grip));
            // The other hand: on the book's edge on its own side, if that edge is by the body; else free.
            var otherHand = drawHand > 0 ? left : right;
            float edgeSide = otherHand != null && sketchPad.InverseTransformPoint(otherHand.position).x < 0f ? -1f : 1f;
            Vector3 edge = sketchPad.TransformPoint(new Vector3(edgeSide * (PadW * .5f + .002f), 0f, -.003f));
            if (Vector3.ProjectOnPlane(edge - at, Vector3.up).magnitude < HandReach && otherHand != null) body.Hold(drawHand < 0, new Pose(edge, body.NaturalGrip(drawHand < 0, edge, 55f)));
            else body.Hold(drawHand < 0, null);
        }

        public const float HandReach = .06f; // metres from Maker's body axis a holding hand may go
        public static bool DrawRightHanded = true; // Maker draws with its right mitten (the left holds the book)
        public int DrawingHand => drawHand;        // checks: 1 right, -1 left, 0 not drawing yet
        // Where the book sits from Maker's feet: sideways (away from the others), toward the person, up;
        // and how much its page faces up (1 = halfway between up and the person). 2026-11-11: held in the lap,
        // a little farther out and flatter (was .05/.03/.8, where the bent head went through the pages); with
        // the posture limit the most bent frame of the clip keeps ~17 mm from the head, the page still faces
        // the person's eyes and the hand still reaches it.
        public static float PadSide = 0f, PadForward = .065f, PadUp = .02f, PadFaceUp = 1.2f;

        private void ReleaseHands()
        {
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null) return;
            var body = WindupBody.Of(maker);
            body?.Hold(true, null);
            body?.Hold(false, null);
        }

        private Transform NewPage()
        {
            var hinge = new GameObject("Page Hinge").transform;
            hinge.SetParent(sketchPad, false);
            hinge.localPosition = new Vector3(0f, PadH * .5f - .004f, -.0002f);
            var sheet = Part("Page", pageMesh);
            sheet.SetParent(hinge, false);
            sheet.localPosition = new Vector3(0f, -(PadH * .5f - .004f), -.0008f);
            var ink = new GameObject("Ink", typeof(MeshFilter), typeof(MeshRenderer));
            ink.transform.SetParent(sheet, false);
            var mesh = new Mesh { name = "Ink" };
            mesh.MarkDynamic();
            owned.Add(mesh);
            ink.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = ink.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pages.Add(hinge);
            return sheet;
        }

        private void ClearPages()
        {
            foreach (var p in pages) if (p != null) Destroy(p.gameObject);
            pages.Clear();
        }

        // ------------------------------------------------------------ what a page shows

        private sealed class PenStroke
        {
            public readonly List<Vector2> points = new List<Vector2>();
            public float width = .0014f;
            public float Length { get { float l = 0f; for (int i = 1; i < points.Count; i++) l += Vector2.Distance(points[i - 1], points[i]); return l; } }
        }

        // Graphite lines as flat ribbons on the page (both sides wound, normals toward the reader).
        private sealed class InkLines
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<int> triangles = new List<int>();
            private readonly List<Color> colors = new List<Color>();
            private readonly List<Vector3> normals = new List<Vector3>();
            private Vector2 last;
            private float width;
            private static readonly Color Graphite = new Color(.27f, .28f, .34f);

            public void Start(Vector2 p, float w) { last = p; width = w; Dot(p); }

            public void To(Vector2 p)
            {
                if ((p - last).sqrMagnitude < 1e-8f) return;
                Vector2 d = (p - last).normalized;
                Vector2 n = new Vector2(-d.y, d.x) * width * .5f;
                // Extend each piece by half a width so the joints overlap.
                Vector2 a = last - d * width * .35f, b = p;
                Quad(a - n, a + n, b - n, b + n);
                last = p;
            }

            private void Dot(Vector2 p)
            {
                float h = width * .5f;
                Quad(p + new Vector2(-h, -h), p + new Vector2(-h, h), p + new Vector2(h, -h), p + new Vector2(h, h));
            }

            private void Quad(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
            {
                int i = vertices.Count;
                const float z = -.00035f;
                vertices.Add(new Vector3(a0.x, a0.y, z)); vertices.Add(new Vector3(a1.x, a1.y, z));
                vertices.Add(new Vector3(b0.x, b0.y, z)); vertices.Add(new Vector3(b1.x, b1.y, z));
                for (int k = 0; k < 4; k++) { colors.Add(Graphite); normals.Add(Vector3.back); }
                triangles.AddRange(new[] { i, i + 1, i + 2, i + 2, i + 1, i + 3, i, i + 2, i + 1, i + 2, i + 3, i + 1 });
            }

            public void Apply(Mesh mesh)
            {
                mesh.Clear();
                mesh.SetVertices(vertices);
                mesh.SetColors(colors);
                mesh.SetNormals(normals);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
            }
        }

        // The shape a look is (from its name and prompt): hex, square, flower or round.
        public static string SketchShape(string words)
        {
            string w = (words ?? "").ToLowerInvariant();
            if (w.Contains("hex")) return "hex";
            if (w.Contains("square") || w.Contains("tile")) return "square";
            if (w.Contains("flower") || w.Contains("petal") || w.Contains("scallop") || w.Contains("daisy") || w.Contains("bloom")) return "flower";
            return "round";
        }

        private float ShapeRadius(string shape, float angle)
        {
            switch (shape)
            {
                case "drawn":
                    if (DrawnOutline == null) return 1f;
                    float at = Mathf.Repeat(angle * Mathf.Rad2Deg, 360f) / 5f;
                    int i0 = Mathf.FloorToInt(at) % 72;
                    return Mathf.Lerp(DrawnOutline[i0], DrawnOutline[(i0 + 1) % 72], at - Mathf.Floor(at));
                case "hex":
                    float a = Mathf.Repeat(angle, Mathf.PI / 3f) - Mathf.PI / 6f;
                    return Mathf.Cos(Mathf.PI / 6f) / Mathf.Cos(a);
                case "square":
                    float c = Mathf.Abs(Mathf.Cos(angle)), s = Mathf.Abs(Mathf.Sin(angle));
                    return .9f / Mathf.Pow(Mathf.Pow(c, 5f) + Mathf.Pow(s, 5f), .2f);
                case "flower":
                    return .9f + .1f * Mathf.Cos(angle * 6f);
                default:
                    return 1f;
            }
        }

        // One page: the look's outline (a little wobbly, overshooting where it closes), the inner rim, a
        // pattern that changes page to page, and a size line with end ticks underneath.
        private List<PenStroke> PagePlan(int page)
        {
            string shape = sketchShapes.Count > 0 ? sketchShapes[page % sketchShapes.Count] : new[] { "round", "hex", "flower" }[page % 3];
            if (sketchOnce && DrawMode == "profile" && DrawnProfile != null) return ProfilePlan();
            if (sketchOnce && DrawnOutline != null) shape = "drawn";
            var random = new System.Random(page * 7919 + 17);
            float R() => (float)random.NextDouble();
            Vector2 c = new Vector2((R() - .5f) * .003f, .003f);
            float r = .0125f;
            float seed = R() * 50f;
            var plan = new List<PenStroke>();

            PenStroke Loop(float radius, float overshoot, float wobble, float width, float start)
            {
                var s = new PenStroke { width = width };
                int n = 72;
                for (int i = 0; i <= n * (1f + overshoot); i++)
                {
                    float t = i / (float)n;
                    float angle = start + t * Mathf.PI * 2f;
                    float rr = radius * ShapeRadius(shape, angle) * (1f + (Mathf.PerlinNoise(seed + t * 3f, radius * 90f) - .5f) * wobble);
                    s.points.Add(c + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * rr);
                }
                return s;
            }
            PenStroke Line(Vector2 a, Vector2 b, float width)
            {
                var s = new PenStroke { width = width };
                int n = Mathf.Max(2, Mathf.CeilToInt(Vector2.Distance(a, b) / .0012f));
                Vector2 bend = new Vector2(-(b - a).y, (b - a).x).normalized * (R() - .5f) * .0006f;
                for (int i = 0; i <= n; i++) { float t = i / (float)n; s.points.Add(Vector2.Lerp(a, b, t) + bend * Mathf.Sin(t * Mathf.PI)); }
                return s;
            }

            plan.Add(Loop(r, .06f, .07f, .0015f, R() * 6f));
            plan.Add(Loop(r * .76f, .03f, .05f, .0011f, R() * 6f));
            switch (page % 4)
            {
                case 0: // ribs
                    for (int i = 0; i < 6; i++)
                    {
                        float angle = i * Mathf.PI / 3f + .3f;
                        Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                        plan.Add(Line(c + dir * r * .22f, c + dir * r * .58f, .001f));
                    }
                    break;
                case 1: // a ring of dots
                    for (int i = 0; i < 8; i++)
                    {
                        float angle = i * Mathf.PI / 4f;
                        Vector2 at = c + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r * .5f;
                        var dot = new PenStroke { width = .0012f };
                        for (int k = 0; k <= 10; k++) { float a2 = k / 10f * Mathf.PI * 2.2f; dot.points.Add(at + new Vector2(Mathf.Cos(a2), Mathf.Sin(a2)) * .0011f); }
                        plan.Add(dot);
                    }
                    break;
                case 2: // a wave around the middle
                {
                    var wave = new PenStroke { width = .001f };
                    for (int i = 0; i <= 96; i++)
                    {
                        float angle = i / 96f * Mathf.PI * 2f;
                        float rr = r * (.46f + .07f * Mathf.Sin(angle * 8f));
                        wave.points.Add(c + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * rr);
                    }
                    plan.Add(wave);
                    break;
                }
                default: // shading hatches along the lower right
                    for (int i = 0; i < 6; i++)
                    {
                        float angle = -1.25f + i * .2f;
                        Vector2 edge = c + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r * ShapeRadius(shape, angle) * 1.06f;
                        plan.Add(Line(edge, edge + new Vector2(.0035f, -.0035f), .0009f));
                    }
                    break;
            }
            // Size line with end ticks.
            float y = c.y - r - .0055f;
            plan.Add(Line(new Vector2(c.x - r, y), new Vector2(c.x + r, y), .0009f));
            plan.Add(Line(new Vector2(c.x - r, y - .0016f), new Vector2(c.x - r, y + .0016f), .0009f));
            plan.Add(Line(new Vector2(c.x + r, y - .0016f), new Vector2(c.x + r, y + .0016f), .0009f));
            return plan;
        }

        // F35 (2026-11-12, chosen design A): a page Maker does not like is torn off, crumpled in both mittens
        // and tossed over its shoulder (it used to be a plain white sphere flying off). The right mitten pulls
        // the page off (crackle) while the left keeps the book; both squeeze it into a faceted paper ball
        // (squeeze sounds, the page shrinking as the ball grows); the right mitten takes it up beside the head
        // and throws it backward; it bounces "tok tok" on the desk and rolls to rest. The tossed pages pile up
        // behind Maker (its effort shows) and all go with a puff when the pictures come out (ClearDrafts).
        // The mittens stay within HandReach (Reach) and clear of the head.
        public const int DraftPile = 5;
        public const float RipSeconds = .35f, CrumpleSeconds = .7f, TossSeconds = .32f;
        private bool tossing;
        private readonly List<PaperBall> draftBalls = new List<PaperBall>();
        public IReadOnlyList<PaperBall> DraftBalls => draftBalls;
        public bool Tossing => tossing;

        private IEnumerator TossDraft(WindupCharacter maker, Transform sheet)
        {
            Drafts++;
            var body = WindupBody.Of(maker);
            if (body == null || sheet == null) { if (sheet != null && sheet.parent != null) { pages.Remove(sheet.parent); Destroy(sheet.parent.gameObject); } yield break; }
            tossing = true;
            bool pencilWas = pencil != null && pencil.gameObject.activeSelf;
            if (pencil != null) pencil.gameObject.SetActive(false);
            // The page leaves the book with its drawing.
            var hinge = sheet.parent;
            sheet.SetParent(transform, true);
            if (hinge != null) { pages.Remove(hinge); Destroy(hinge.gameObject); }
            Vector3 Toward() { var t = Head != null ? Vector3.ProjectOnPlane(Head.position - maker.transform.position, Vector3.up) : Vector3.back; return t.sqrMagnitude > 1e-4f ? t.normalized : Vector3.back; }
            Pose Grip(Vector3 p) => new Pose(Reach(maker, p), Quaternion.LookRotation(Toward() + Vector3.up * .3f, Vector3.up));
            Vector3 BookEdge() => sketchPad != null ? sketchPad.TransformPoint(new Vector3(-(PadW * .5f + .002f), 0f, -.003f)) : maker.transform.position + Vector3.up * .05f;
            // Picked up, or on the move (walking, jumping, falling): no tearing and tossing while its body goes elsewhere.
            bool Gone() => maker == null || maker.movement.IsHeld ||
                           (!maker.movement.MeetingSeatLocked && maker.movement.State != RoomCapsule.CapsulePetController.MotionState.Idle);
            body.Emote(WindupBody.Focused, RipSeconds + CrumpleSeconds);

            // 1. Rip: the right mitten pulls the page up and out to its side; the left keeps the book.
            Vector3 start = sheet.position;
            Quaternion startRot = sheet.rotation;
            ToySounds.Play(ToySounds.Cue.Peel, start, .5f, 1.7f);
            for (float t = 0f; t < 1f && !Gone(); t += Time.deltaTime / RipSeconds)
            {
                Vector3 at = maker.transform.position, toward = Toward(), right = Vector3.Cross(Vector3.up, toward);
                Vector3 to = OutOfHead(body, at + toward * .05f + right * .04f + Vector3.up * .088f, right);
                float k = Mathf.SmoothStep(0f, 1f, t);
                Vector3 p = OutOfHead(body, Vector3.Lerp(start, to, k), right);
                sheet.SetPositionAndRotation(p, Quaternion.Slerp(startRot, Quaternion.LookRotation(-toward, Vector3.up), k) * Quaternion.Euler(0f, 0f, Mathf.Sin(k * Mathf.PI) * 18f));
                body.Hold(true, Grip(p + right * .014f + Vector3.up * .01f));
                body.Hold(false, Grip(BookEdge()));
                yield return null;
            }

            // 2. Crumple: both mittens bring it to the chest and squeeze three times; the page shrinks away as
            //    the paper ball grows between them.
            var ball = PaperBall.Make(transform, material, Drafts);
            ball.transform.localScale = Vector3.zero;
            Vector3 from = sheet.position;
            int squeezes = 0;
            for (float t = 0f; t < 1f && !Gone(); t += Time.deltaTime / CrumpleSeconds)
            {
                Vector3 at = maker.transform.position, toward = Toward(), right = Vector3.Cross(Vector3.up, toward);
                Vector3 chest = OutOfHead(body, at + toward * .046f + Vector3.up * .062f, toward);
                float squeeze = Mathf.Abs(Mathf.Sin(t * Mathf.PI * 3f));
                if (t * 3f >= squeezes + .5f && squeezes < 3) { squeezes++; ToySounds.Play(ToySounds.Cue.Paper, chest, .35f, 1.15f + squeezes * .15f); }
                Vector3 c = OutOfHead(body, Vector3.Lerp(from, chest, Mathf.SmoothStep(0f, 1f, Mathf.Min(1f, t * 2.5f))), toward);
                float page = Mathf.Lerp(1f, .12f, Mathf.SmoothStep(0f, 1f, t * 1.4f));
                sheet.position = c;
                sheet.localScale = new Vector3(page, page, 1f);
                sheet.rotation *= Quaternion.Euler(UnityEngine.Random.Range(-6f, 6f), UnityEngine.Random.Range(-6f, 6f), UnityEngine.Random.Range(-10f, 10f));
                if (t > .65f && sheet.gameObject.activeSelf) sheet.gameObject.SetActive(false);
                ball.transform.position = c;
                ball.transform.localScale = Vector3.one * PaperBall.Radius * Mathf.SmoothStep(0f, 1f, (t - .15f) / .6f) * (1f - .12f * squeeze);
                ball.transform.rotation *= Quaternion.Euler(0f, 220f * Time.deltaTime, 0f);
                float gap = .017f - .006f * squeeze;
                body.Hold(true, Grip(c + right * gap));
                body.Hold(false, Grip(c - right * gap));
                yield return null;
            }
            Destroy(sheet.gameObject);
            ball.transform.localScale = Vector3.one * PaperBall.Radius;

            // 3. Toss: up beside the head on its right (clear of the head), then a flick backward.
            Vector3 hold = ball.transform.position;
            for (float t = 0f; t < 1f && !Gone(); t += Time.deltaTime / TossSeconds)
            {
                Vector3 at = maker.transform.position, toward = Toward(), right = Vector3.Cross(Vector3.up, toward);
                Vector3 over = OutOfHead(body, at - toward * .012f + right * .048f + Vector3.up * .132f, right);
                // Out to the side first, then up beside the head (a straight line would cross the face).
                float k = Mathf.SmoothStep(0f, 1f, t);
                Vector3 side = hold + right * .03f * Mathf.Sin(k * Mathf.PI) * 1.2f;
                Vector3 p = OutOfHead(body, Vector3.Lerp(side, over, k), right);
                ball.transform.position = p;
                body.Hold(true, Grip(p - Vector3.up * .006f));
                body.Hold(false, Grip(BookEdge()));
                yield return null;
            }
            {
                Vector3 toward = Toward(), right = Vector3.Cross(Vector3.up, toward);
                ball.Throw(-toward * .42f + right * (Drafts % 2 == 0 ? .1f : .2f) + Vector3.up * .7f, UnityEngine.Random.onUnitSphere * 14f);
                ToySounds.Play(ToySounds.Cue.Whoosh, ball.transform.position, .3f, 1.7f);
                if (Drafts == 1) Caption(Crew.Maker, "Nope!", 1.6f);
                draftBalls.Add(ball);
                while (draftBalls.Count > DraftPile)
                {
                    var old = draftBalls[0];
                    draftBalls.RemoveAt(0);
                    if (old != null) { ToyFx.Puff(old.transform.position, .006f, 2); Destroy(old.gameObject); }
                }
            }
            // The follow-through, then back to the pencil.
            for (float t = 0f; t < 1f && !Gone(); t += Time.deltaTime / .2f)
            {
                Vector3 at = maker.transform.position, toward = Toward(), right = Vector3.Cross(Vector3.up, toward);
                body.Hold(true, Grip(at + toward * .03f + right * .045f + Vector3.up * .1f));
                yield return null;
            }
            if (pencil != null && pencilWas) pencil.gameObject.SetActive(true);
            tossing = false;
        }

        // A point the paper passes through, moved out along `away` until it (with the ball's size) is clear of
        // Maker's big head as it is posed now (bent over its book, the head comes down in front of its chest).
        private static Vector3 OutOfHead(WindupBody body, Vector3 p, Vector3 away)
        {
            for (int i = 0; i < 20 && body.HeadDepth(p) < 1.3f; i++) p += away * .004f;
            return p;
        }

        // The tossed pages vanish one after another with a little puff.
        private void ClearDrafts()
        {
            float delay = 0f;
            foreach (var ball in draftBalls)
            {
                if (ball == null) continue;
                var b = ball;
                StartCoroutine(After(delay, () => { if (b != null) { ToyFx.Puff(b.transform.position, .006f, 2); ToySounds.Play(ToySounds.Cue.Pop, b.transform.position, .15f, 1.9f); Destroy(b.gameObject); } }));
                delay += .08f;
            }
            draftBalls.Clear();
        }

        // The finished pictures pop out of Maker's book, one after another, up to their row.
        private IEnumerator RevealLooks(Vector3 from)
        {
            foreach (var p in Looks)
            {
                if (p.root == null) continue;
                p.root.position = from;
                p.root.rotation = SpeechBubbleDock.FacingAt(from);
                p.root.localScale = Vector3.one * .15f;
                p.root.gameObject.SetActive(true);
                StartCoroutine(Grow(p.root, 1f, .4f));
                ToySounds.Play(ToySounds.Cue.Pop, from, .5f, 1.2f + .1f * Looks.IndexOf(p));
                ToyFx.Puff(from, .01f, 3);
                yield return new WaitForSeconds(Scripted ? .08f : .18f);
            }
            LooksShown = true;
        }

        private static IEnumerator Grow(Transform target, float size, float seconds)
        {
            Vector3 start = target.localScale;
            for (float t = 0f; t < 1f && target != null; t += Time.deltaTime / seconds)
            {
                float k = 1f + Mathf.Sin(t * Mathf.PI) * .12f * (1f - t);
                target.localScale = Vector3.Lerp(start, Vector3.one * size, Mathf.SmoothStep(0f, 1f, t)) * k;
                yield return null;
            }
            if (target != null) target.localScale = Vector3.one * size;
        }

        // Short, instant reactions (no voice delay): a caption, a hop and a sound.
        private void Ack(Crew crew, string text)
        {
            if (AckCalm(crew, text)) return; // F76: filler is a hop and a mumble; anything to read waits first in line
            Caption(crew, text, 2.2f);
            Talk.Add(MakerRoles.Name(crew) + ": " + text); // F48
            if (cast.TryGetValue(crew, out var c) && c != null)
            {
                c.SetCue("encourage");
                WindupBody.Express(c, text, 1.6f);
                StartCoroutine(After(.8f, () => { if (c.Cue == "encourage") c.SetCue("idle"); }));
                ToySounds.Play(ToySounds.Cue.Pop, c.transform.position + Vector3.up * .15f, .45f, 1.3f);
            }
        }

        // ------------------------------------------------------------ frame and input

        // The person starts to talk: whoever is speaking stops, what was queued is dropped, and the crew
        // listens (WindupBody.UserTalking turns them to the person); what they say decides what comes next.
        private bool userWasTalking;
        public int BargeIns { get; private set; }
        private void UserStartedTalking()
        {
            BargeIns++;
            Voice.Stop();
            StopMumble(); // F76
            Hush();
            HideCaption();
            captionUntil = 0f;
            nextLineAt = Time.time + .6f;
            Heard();
        }

        private void Update()
        {
            // F89: the hands' rays bend onto a button they just miss, before anything reads them this frame.
            try { AimAssistFrame(); } catch (Exception exception) { LogStepError("aim assist", exception); }
            // F45: the Find ideas button works before the session begins too (at the first note).
            try { IdeasButtonFrame(); } catch (Exception exception) { LogStepError("ideas button", exception); }
            // F55: the teammates' tools and Fixer's settings box (also in the introduction, before Begin).
            try { CrewToolsFrame(); } catch (Exception exception) { LogStepError("crew tools", exception); }
            try { IntroPagerFrame(); } catch (Exception exception) { LogStepError("intro pager", exception); } // the introduction's < >
            try { TopicButtonsFrame(); } catch (Exception exception) { LogStepError("topic buttons", exception); } // F72: the note's Rewrite and check
            try { RaisedPillsFrame(); } catch (Exception exception) { LogStepError("buttons", exception); } // F77: raised buttons sink, light and spring back
            try { RePickFrame(); } catch (Exception exception) { LogStepError("re-pick", exception); } // F81: Change sketch on the chosen picture
            try { PlatePagerFrame(); } catch (Exception exception) { LogStepError("plate pager", exception); } // F94: < v2 > on the stand
            if (!Begun) { try { EarlyHandsFrame(); } catch (Exception exception) { LogStepError("early hands", exception); } return; } // F97: things put back on the way in
            bool userTalking = arrival.microphone != null && (arrival.microphone.IsHolding || arrival.microphone.IsCapturing);
            if (userTalking && !userWasTalking) UserStartedTalking();
            userWasTalking = userTalking;
#if UNITY_EDITOR
            if (!UnityEngine.XR.XRSettings.isDeviceActive) EditorInput();
#endif
            // (2026-11-12) Each part runs on its own: one that throws is logged (maker_input_log.txt) and the
            // others go on. Holding the open notebook only keeps new pinches from doing other things.
            RunFrameSteps();
            InputLogFrame();
            bool bookHeld = NotebookHeld || SketchbookHeld || takingSketchbook;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand))
                {
                    if (hand != null)
                    {
                        wasPinching.Remove(hand); thumbsSince.Remove(hand);
                        if (holdHand == hand) LetGo();
                        if (carryHand == hand && carried != null) DropFlag();   // F40
                        if (knobHand == hand && knobAxis >= 0) EndKnob();      // F39
                        if (panelHand == hand) EndPanel(false);                 // 2026-11-13: a carried picture row stays
                    }
                    continue;
                }
                bool pinching = HandIO.Pinching(hand);
                wasPinching.TryGetValue(hand, out bool before);
                wasPinching[hand] = pinching;
                Vector3 point = PinchPoint(hand);
                if (carryHand == hand && carried != null) { CarryFrame(hand, pinching, point); continue; } // F40: a flag in the fingers
                if (knobHand == hand && knobAxis >= 0) { KnobFrame(hand, pinching, point); continue; }   // F39: v2's size knob
                if (panelHand == hand && panelGroup != PanelGroup.None)                                  // 2026-11-13: a picture row in the fingers
                {
                    bool aimedPanel = HandIO.Pointer(hand, out Pose panelPointer);
                    PanelFrame(pinching, point, aimedPanel ? new Ray(panelPointer.position, panelPointer.forward) : (Ray?)null);
                    continue;
                }
                if (holdHand == hand)
                {
                    if (pinching) Hold(point, HandTurn(hand)); else LetGo();
                    continue;
                }
                if (FarPendingFrame(hand, pinching, point)) continue; // F66: an aimed pinch waiting to become a far move
                if (pinching && !before && CardShown && (!HandRules || HeldBy(hand) == null)) // F77: the lesson card's Skip (also while measuring)
                {
                    bool aimedAtSkip = HandIO.Pointer(hand, out Pose skipPointer);
                    if (CardSkipPinch(point, aimedAtSkip ? new Ray(skipPointer.position, skipPointer.forward) : (Ray?)null)) { lastPinchBy = "lesson skip"; LogPinch(hand, point); continue; }
                }
                if (pinching && !before && CanRePick && (!HandRules || HeldBy(hand) == null)) // F81: Change sketch on the chosen picture
                {
                    bool aimedAtRePick = HandIO.Pointer(hand, out Pose rePickPointer);
                    if (RePickPinch(point, aimedAtRePick ? new Ray(rePickPointer.position, rePickPointer.forward) : (Ray?)null)) { lastPinchBy = "change sketch"; LogPinch(hand, point); continue; }
                }
                if (pinching && !before && PlatePagerShown && (!HandRules || HeldBy(hand) == null)) // F94: < > on the stand
                {
                    bool aimedAtPager = HandIO.Pointer(hand, out Pose pagerPointer);
                    if (PlatePagerPinch(point, aimedAtPager ? new Ray(pagerPointer.position, pagerPointer.forward) : (Ray?)null)) { lastPinchBy = "stand pager"; LogPinch(hand, point); continue; }
                }
                if (pinching && !before && !bookHeld && Question != null && (!HandRules || HeldBy(hand) == null)) // F66: not by a hand that holds something
                {
                    bool aimedAtReply = HandIO.Pointer(hand, out Pose replyPointer);
                    if (QuestionPinch(point, aimedAtReply ? new Ray(replyPointer.position, replyPointer.forward) : (Ray?)null)) { lastPinchBy = "reply"; LogPinch(hand, point); continue; } // F44
                }
                if (pinching && !before && Current != Step.Measuring && !bookHeld && !(CollectHandBusy != null && CollectHandBusy(hand))) // F91: not a hand carrying a collected thing
                {
                    bool aimed = HandIO.Pointer(hand, out Pose pointer);
                    Ray? pinchRay = aimed ? new Ray(pointer.position, pointer.forward) : (Ray?)null;
                    if (!FarGate(hand, point, ref pinchRay)) { WriteInputLog("pinch " + hand.Handedness + " aimed from afar: waiting " + FarHold.ToString("0.##") + " s"); UpdateThumbs(hand); continue; } // F66
                    try { OnPinch(hand, point, pinchRay); }
                    catch (Exception exception) { LogStepError("pinch", exception); }
                    LogPinch(hand, point);
                }
                UpdateThumbs(hand);
            }
            float lost = Head != null ? Head.position.y - 3f : -3f;
            foreach (var thing in MadeThings())
            {
                if (thing == held || thing.position.y >= lost) continue;
                var body = thing.GetComponent<Rigidbody>();
                Vector3 back = thing == Product && HasSpot ? MugSpot : DeskPointInFront(); // fell through the room: bring it back
                if (body != null) { body.isKinematic = true; body.position = back; }
                thing.position = back;
            }
        }

        private void OnPinch(Hand hand, Vector3 point, Ray? ray)
        {
            Heard();
            // (lastPinchBy: who took the pinch, for maker_input_log.txt)
            lastPinchBy = "mic"; if (arrival.MicNear(point) && (!HandRules || arrival.MicHeld || MayGrabMic(hand, point))) return; // the mic is picked up by the arrival
            lastPinchBy = "settings"; if (SettingsPinch(point, ray)) return; // F55: Fixer's settings box
            lastPinchBy = "wall button"; if (WallButtonPinch(hand, point, ray)) return; // F72: Rewrite on the note, the x on a picture or card
            lastPinchBy = "flag"; if (FlagTaps && FlagPinchFirst(hand, point)) return; // F102: a flag in reach before v1, v2 and the stand
            if (NearestPinch(hand, point)) return; // F60: the nearest of a card, Spark's frame, the held-out book or a made thing
            lastPinchBy = "shape card"; if (ShapeCardPinch(hand, point, null)) return; // F56: a card or the Done pill, near
            lastPinchBy = "show frame"; if (ShowFramePinch(hand, point, null)) return; // F56: Spark's frame, near (rim, corner, button)
            // F89: the page's check and undo from afar before the page's grab rim (their reach overlaps the rim near the
            // corner; the ring shows the button there, FarAim, so the pinch presses it).
            lastPinchBy = "page button (far)"; if (Drawing && PageButtonPinch(ray)) return;
            lastPinchBy = "sketchbook"; if (SketchbookPinch(hand, point, ray)) return;
            lastPinchBy = "crew tool"; if (CrewToolPinch(point, ray)) return; // F55: a tool aimed at from afar
            lastPinchBy = "wall note"; if (WallNotePinch(hand, point, ray)) return;
            lastPinchBy = "notebook"; if (NotebookPinch(point, ray, hand)) return;
            lastPinchBy = "solid"; if (SolidPinch(hand, point, ray)) return; // F59: the clay being sculpted, or a cut's see-through part
            lastPinchBy = "page button (far)"; if (PageButtonPinch(ray)) return; // F68: the page's check or eraser from afar
            lastPinchBy = Capturing ? "colour pick" : "tools"; if (ToolsPinch(hand, point, ray)) { if (Capturing) lastPinchBy = "colour pick"; else if (Dragging != null) lastPinchBy = "swatch drag"; return; }
            lastPinchBy = "v2 knob"; if (KnobPinch(hand, point, ray)) return; // F39: a size knob on v2
            var flag = FlagNear(point, ray);
            lastPinchBy = "flag"; if (flag != null) { PickFlag(flag, hand, point, ray); return; } // F40: flags are picked up and put anywhere
            lastPinchBy = "paper ball"; if (NoteOnWall && PinBallPinch(hand, point)) return; // F72: a crumpled picture picked up again
            var pin = PinNear(point, ray, out var pinView);
            lastPinchBy = "moodboard pin"; if (pin != null) { if (NoteOnWall) CarryPin(hand, point, ray, pin, pinView); else UsePin(pin, pinView); return; } // F72: grab & release
            var near = MadeNear(point);
            lastPinchBy = "made thing"; if (near != null) { Grab(hand, point, near); return; }
            lastPinchBy = "sticker"; if (StickerPinch(point, ray)) return; // F47: a sticker opens its note (it never picks its picture)
            var idea = ChoiceAt(point) ?? (ray.HasValue ? ChoiceOnRay(ray.Value, out _) : null);
            lastPinchBy = "idea note"; if (idea != null) { PickIdea(idea); return; }
            // 2026-11-13: the look row and the review pictures are carried by a pinch that moves; one that does not is a
            // tap (a look is picked when the pinch lets go, MakerSession.PanelGrab.cs).
            lastPinchBy = "picture"; if (PanelPinch(hand, point, ray)) return;
            var picture = PictureAt(point) ?? (ray.HasValue ? PictureOnRay(ray.Value, out _) : null);
            if (picture != null && Current == Step.Looks && Looks.Contains(picture)) { PickLook(picture); return; }
            // F56: the hand's ray on a shape card, the Done pill or Spark's frame (only when nothing nearer took the pinch).
            lastPinchBy = "shape card (far)"; if (ray.HasValue && ShapeCardPinch(hand, Vector3.positiveInfinity, ray)) return;
            lastPinchBy = "show frame (far)"; if (ray.HasValue && ShowFramePinch(hand, Vector3.positiveInfinity, ray)) return;
            lastPinchBy = "nothing";
            if (ray.HasValue && MadeOnRay(ray.Value, out float far) is Transform aimed && far > .25f) { lastPinchBy = "made thing (far)"; Grab(hand, point, aimed, far); return; }
            // F66: nothing took it: with the palette on the left hand, a right-hand pinch picks a colour.
            if (HandRules && ColourHand(hand) && !Drawing) { BeginCapture(hand); lastPinchBy = "colour pick"; }
        }

        private Picture PictureAt(Vector3 point)
        {
            if (Current != Step.Looks) return null;
            foreach (var p in Looks)
            {
                if (p.root == null || !p.root.gameObject.activeInHierarchy) continue;
                Vector3 local = p.root.InverseTransformPoint(point);
                if (Mathf.Abs(local.x) < p.size * .6f && Mathf.Abs(local.y) < p.size * .65f && Mathf.Abs(local.z) < .04f) return p;
            }
            return null;
        }

        public Picture PictureOnRay(Ray ray, out float distance)
        {
            distance = float.PositiveInfinity;
            Picture best = null;
            if (Current != Step.Looks) return null;
            foreach (var p in Looks)
            {
                if (p.root == null || !p.root.gameObject.activeInHierarchy) continue;
                var plane = new Plane(p.root.forward, p.root.position);
                if (!plane.Raycast(ray, out float d) || d <= 0f || d >= distance) continue;
                Vector3 local = p.root.InverseTransformPoint(ray.GetPoint(d));
                if (Mathf.Abs(local.x) > p.size * .6f || Mathf.Abs(local.y) > p.size * .65f) continue;
                distance = d;
                best = p;
            }
            return best;
        }

        private Choice ChoiceAt(Vector3 point)
        {
            if (Current != Step.Suggesting) return null;
            foreach (var c in Ideas)
            {
                Vector3 local = c.root.InverseTransformPoint(point);
                if (Mathf.Abs(local.x) < c.width * .6f && Mathf.Abs(local.y) < c.height * .8f && Mathf.Abs(local.z) < .04f) return c;
            }
            return null;
        }

        public Choice ChoiceOnRay(Ray ray, out float distance)
        {
            distance = float.PositiveInfinity;
            Choice best = null;
            if (Current != Step.Suggesting) return null;
            foreach (var c in Ideas)
            {
                var plane = new Plane(c.root.forward, c.root.position);
                if (!plane.Raycast(ray, out float d) || d <= 0f || d >= distance) continue;
                Vector3 local = c.root.InverseTransformPoint(ray.GetPoint(d));
                if (Mathf.Abs(local.x) > c.width * .6f || Mathf.Abs(local.y) > c.height * .8f) continue;
                distance = d;
                best = c;
            }
            return best;
        }

        // RemoteGrab and the Editor preview leave pinches on the pictures and idea cards to the session.
        public bool UiHit(Ray ray, out float distance)
        {
            distance = 0f;
            // F67: the introduction's < > arrows (before the session begins) show the far cursor too.
            if (PagerShown && PagerPinch(Vector3.positiveInfinity, ray) != 0 && new Plane(pagerRoot.forward, pagerRoot.position).Raycast(ray, out distance)) return true;
            if (TopicButtonOnRay(ray, out distance)) return true; // F72: the note's buttons before the make begins
            if (!Begun) return IdeasOnRay(ray, out distance); // F89: Find ideas at the first note shows the ring too
            if (FarButtonOnRay(ray, out distance)) return true; // F68
            if (SketchbookOnRay(ray, out distance)) return true;
            if (CardOnRay(ray, out distance) != null || FrameOnRay(ray, out distance)) return true; // F56
            if (NotebookOnRay(ray, out distance)) return true;
            if (KnobNear(Vector3.positiveInfinity, ray) >= 0 || FlagNear(Vector3.positiveInfinity, ray) != null || PinNear(Vector3.positiveInfinity, ray, out _) != null) { distance = .5f; return true; }
            if (PictureOnRay(ray, out distance) != null) return true;
            if (ChoiceOnRay(ray, out distance) != null) return true;
            return MadeOnRay(ray, out distance) != null;
        }

        public static float ThumbsHold = .7f;
        private void UpdateThumbs(Hand hand)
        {
            // (Things are built as soon as they are planned: a thumbs up only confirms a measurement.)
            // F100 (Quest 2026-11-15, the user: "it made the shape without the tape - no tape lesson, I never measured"):
            // the log had W 100 at 32.9 s, H 100 at 33.3 s and the size box's Done at 33.8 s, half a second after the
            // measuring step began: a hand resting in a thumbs-up-like pose (a loose fist, thumb up) took the TYPICAL size
            // (Hint) as the person's every 0.4 s, and then pressed Done. Now a thumbs up only confirms a length the hands
            // just measured (its bar filling, Settling), never the typical size, never the box's Done, and it is held
            // ThumbsHold.
            bool wants = (Current == Step.Measuring && HasMeasure && Settling && SizeBoxPhase != BoxPhase.Ready) || Confirming; // F40: a thumbs up is the yes
            if (!wants || !HandIO.ThumbsUp(hand)) { thumbsSince.Remove(hand); return; }
            if (!thumbsSince.TryGetValue(hand, out float since)) { thumbsSince[hand] = Time.time; return; }
            if (Time.time - since < ThumbsHold) return;
            thumbsSince.Remove(hand);
            if (Confirming) { BuildFromDraft(); return; }
            WriteInputLog("thumbs up: the measured " + Measured.ToString("0.#") + " mm confirmed", true);
            ConfirmMeasure();
        }

        private static Vector3 PinchPoint(Hand hand)
        {
            bool thumb = HandIO.Joint(hand, HandJointId.HandThumbTip, out Pose a);
            bool index = HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose b);
            return thumb && index ? (a.position + b.position) * .5f : index ? b.position : a.position;
        }

        private static Vector3 Tip(Hand hand) => HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose p) ? p.position : PinchPoint(hand);
        private static Quaternion HandTurn(Hand hand) => hand != null && HandIO.Root(hand, out Pose root) ? root.rotation : Quaternion.identity;

        // Above the crew (lift metres over their feet), a little toward the user, with the row's sideways
        // axis: pictures and cards sit over the characters, not between them and the user.
        private Vector3 AboveCrew(float lift, out Vector3 side)
        {
            var feet = cast.Values.Where(c => c != null).Select(c => c.transform.position).ToList();
            if (feet.Count == 0) return FrontPoint(.5f, -.14f, out side);
            Vector3 center = feet.Aggregate(Vector3.zero, (a, b) => a + b) / feet.Count;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - center, Vector3.up) : Vector3.back;
            toUser = toUser.sqrMagnitude > .0001f ? toUser.normalized : Vector3.back;
            side = Vector3.Cross(Vector3.up, -toUser);
            // Over the highest one (some may stand on the floor, others on the desk).
            center.y = feet.Max(f => f.y);
            // A little behind the crew, so their speech bubbles (nearer the person) draw in front.
            return center + Vector3.up * lift - toUser * .07f;
        }

        // The look row is placed right on top of Maker's speech bubble, centred over Maker and a little behind it so
        // the bubble draws in front. 2026-11-13 (the user: "the 3 pictures keep following Maker"): it is placed once
        // per row and then stays where it is (the person may carry it, MakerSession.PanelGrab.cs); it only steps back
        // along the line of sight while it would cover a speech card.
        public Vector3 RowCenter { get; private set; }
        private void KeepRowOverBubble()
        {
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null || Looks.Count == 0 || Looks[0].root == null) return;
            float total = 0f;
            foreach (var look in Looks) total += RowWidthOf(look);
            if (!RowPlacedFor(Looks[0].root))
            {
                Vector3 at = maker.transform.position;
                Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - at, Vector3.up) : Vector3.back;
                toUser = toUser.sqrMagnitude > .0001f ? toUser.normalized : Vector3.back;
                var body = WindupBody.Of(maker);
                float top = (body != null ? body.HeadTop.y : at.y + .156f) + SpeechLift + .045f;
                var bubble = maker.authoredBubble;
                if (bubble != null && bubble.gameObject.activeInHierarchy)
                    foreach (var r in bubble.GetComponentsInChildren<Renderer>())
                        if (r.enabled && r.gameObject.activeInHierarchy && !(r is ParticleSystemRenderer)) top = Mathf.Max(top, r.bounds.max.y);
                // F44: above an open question's reply pills; with none open yet, room for the pick prompt's pills that
                // come right after (the row no longer rises to make room once it is up).
                float pills = QuestionTopNear(at, .5f);
                top = pills > float.NegativeInfinity && pills > top ? pills : top + ChipHeight + .014f;
                // F52: nothing hangs under the pictures any more (stickers on their corners, notes over the picture,
                // pros/cons on the sides): only the frame's name below. The weighed look gets room for its side cards.
                float half = Looks[0].size * .5f + .05f;
                rowAnchor = VisiblePanelSpot(new Vector3(at.x, top + .012f + half, at.z) - toUser * .03f,
                    total * .5f, (half + Looks[0].size * .5f) * .5f + .02f, half);
                // Fixed once the pictures come out of the book (before that it is only where they will fly to).
                bool revealed = false;
                foreach (var look in Looks) revealed |= look.root != null && look.root.gameObject.activeSelf;
                rowFor = revealed ? Looks[0].root : null;
                rowMovedByPerson = false;
                rowPush = rowPushWant = 0f;
            }
            // F44: never over an open question's pills: until the person has moved the row, it only ever rises
            // above them (it does not follow Maker about, and it does not come down again by itself).
            bool reached = rowFor != null && RowReached(); // F80: the row does not move under a reaching hand
            if (rowFor != null && !rowMovedByPerson && Question != null && !Question.answered && Question.root != null && !reached)
            {
                float pillsTop = PillsTop(Question);
                Vector3 off = Question.root.position - rowAnchor;
                float rowBottom = rowAnchor.y - (Looks[0].size * .5f + .05f);
                if (pillsTop > rowBottom - .012f && new Vector2(off.x, off.z).magnitude < total * .5f + .12f)
                    rowAnchor.y += pillsTop + .012f - rowBottom;
            }
            RowCenter = rowAnchor + PushDir(rowAnchor) * rowPush;
            var rowFacing = SpeechBubbleDock.FacingAt(RowCenter);
            Vector3 side = rowFacing * Vector3.right;
            float x = -total * .5f;
            for (int i = 0; i < Looks.Count; i++)
            {
                float w = RowWidthOf(Looks[i]);
                Looks[i].home = RowCenter + side * (x + w * .5f);
                x += w;
            }
        }

        // The chosen picture stays small beside Maker (on the side away from the others, a little behind),
        // never over its head where its speech bubble is.
        private Vector3 BesideMaker()
        {
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null) return AboveCrew(ChosenLift, out _);
            Vector3 at = maker.transform.position;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - at, Vector3.up) : Vector3.back;
            toUser = toUser.sqrMagnitude > .0001f ? toUser.normalized : Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, toUser);
            var others = cast.Where(c => c.Key != Crew.Maker && c.Value != null).Select(c => c.Value.transform.position).ToList();
            if (others.Count > 0 && Vector3.Dot(others.Aggregate(Vector3.zero, (a, b) => a + b) / others.Count - at, side) > 0f) side = -side;
            return at + side * .17f + Vector3.up * ChosenLift - toUser * .05f;
        }

        // F102 (Quest 2026-11-16, the user: "the picture I picked is at the characters' height, I can't see it - and then
        // another 'approved' picture comes up; why a second one?"): the chosen picture stands ChosenBelowEyes under the
        // eyes (never lower than ChosenLift over Maker's feet), ChosenSide to Maker's side away from the others and a
        // little toward the person, where the room leaves it in view; it steps back from speech cards it covers
        // (PanelGuardFrame) and is the one picture of the shape: the shape review shows its picture on it (no
        // "Approved shape" copy). false keeps BesideMaker and the copy (older checks).
        public static bool ChosenAtEyes = true;
        public const float ChosenBelowEyes = .24f, ChosenSide = .26f, ChosenToward = .06f, ChosenScale = .85f;
        private Vector3 ChosenHome(Picture picture)
        {
            if (Head == null || !cast.TryGetValue(Crew.Maker, out var maker) || maker == null) return BesideMaker();
            Vector3 at = maker.transform.position;
            Vector3 toUser = Vector3.ProjectOnPlane(Head.position - at, Vector3.up);
            toUser = toUser.sqrMagnitude > .0001f ? toUser.normalized : Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, toUser);
            var others = cast.Where(c => c.Key != Crew.Maker && c.Value != null).Select(c => c.Value.transform.position).ToList();
            if (others.Count > 0 && Vector3.Dot(others.Aggregate(Vector3.zero, (a, b) => a + b) / others.Count - at, side) > 0f) side = -side;
            float y = Mathf.Max(Head.position.y - ChosenBelowEyes, at.y + ChosenLift);
            float half = (picture != null ? picture.size : .17f) * ChosenScale * .5f;
            // (the picture and the Change sketch button that hangs under it)
            bool Clear(Vector3 p) => PanelVisible(p - Vector3.up * .02f, half + .01f, half + .045f, SpeechBubbleDock.FacingAt(p));
            // Beside Maker (this side, then the other), nearer the person if the room is in the way, a little higher...
            foreach (float s in ChosenSides)
                foreach (float toward in ChosenTowards)
                    foreach (float up in ChosenUps)
                    {
                        Vector3 p = at + side * (s * ChosenSide) + toUser * toward;
                        p.y = y + up;
                        if (Clear(p)) { ChosenPlacedHow = "beside Maker"; return p; }
                    }
            // ... else in front of the person, a little under the eyes.
            foreach (float ahead in ChosenAheads)
                foreach (float below in ChosenBelows)
                {
                    Vector3 p = FrontPoint(ahead, below, out var across) + across * (Vector3.Dot(at - Head.position, across) > 0f ? .16f : -.16f);
                    if (Clear(p)) { ChosenPlacedHow = "in front, under the eyes"; return p; }
                }
            Vector3 want = at + side * ChosenSide + toUser * ChosenToward;
            want.y = y;
            ChosenPlacedHow = "any clear spot";
            return VisiblePanelSpot(want, half + .01f, half + .02f);
        }

        private static readonly float[] ChosenSides = { 1f, -1f }, ChosenTowards = { ChosenToward, .15f, .25f }, ChosenUps = { 0f, .08f },
            ChosenAheads = { .6f, .5f, .4f }, ChosenBelows = { -ChosenBelowEyes, -.15f, -.08f };
        public string ChosenPlacedHow { get; private set; } = ""; // (checks)

        private Vector3 AboveCharacter(Crew crew, float lift)
        {
            if (!cast.TryGetValue(crew, out var c) || c == null) return AboveCrew(lift, out _);
            Vector3 at = c.transform.position;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - at, Vector3.up) : Vector3.back;
            toUser = toUser.sqrMagnitude > .0001f ? toUser.normalized : Vector3.back;
            return at + Vector3.up * lift + toUser * .05f;
        }

        // A point in front of the user (metres ahead, metres below the eyes) and the sideways axis.
        private Vector3 FrontPoint(float ahead, float below, out Vector3 side)
        {
            var head = Head;
            Vector3 forward = head != null ? Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized : Vector3.forward;
            if (forward.sqrMagnitude < .01f) forward = Vector3.forward;
            side = Vector3.Cross(Vector3.up, forward);
            Vector3 eye = head != null ? head.position : new Vector3(0f, 1.5f, 0f);
            return eye + forward * ahead + Vector3.up * below;
        }

        private Vector3 DeskPointInFront()
        {
            // (2026-11-12) On the working desk (the nearest point of it), never the floor below the point in front.
            return WorkPoint(FrontPoint(.4f, -.35f, out _), .06f);
        }

        private RoomCapsule.CapsuleRoomSurface SurfaceAt(Vector3 point)
        {
            var room = arrival.handoff.meeting.environment;
            RoomCapsule.CapsuleRoomSurface best = null;
            float bestY = float.NegativeInfinity;
            if (room == null) return null;
            foreach (var surface in room.Surfaces)
            {
                if (surface.Polygon == null) continue;
                var xz = new Vector2(point.x, point.z);
                if (!surface.Contains(xz, -.01f)) continue;
                float y = surface.Point(xz).y;
                if (y < point.y + .06f && y > bestY) { bestY = y; best = surface; }
            }
            return best;
        }

        // The table surface under a point (MRUK), or just below where the hand was.
        private float DeskHeightAt(Vector3 point, float fallback)
        {
            var surface = SurfaceAt(point);
            return surface != null ? surface.Point(new Vector2(point.x, point.z)).y : fallback - .005f;
        }

#if UNITY_EDITOR
        // Mouse: click an idea card or a picture; drag between two points to measure; drag the made
        // object and let go to drop it. Keys: Enter = okay / make it, P = print, L = what do you see, G = look around.
        private Vector3 mouseFrom;
        private bool mouseTape;
        private void EditorInput()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var camera = Camera.main;
            if (mouse != null && camera != null)
            {
                var ray = camera.ScreenPointToRay(mouse.position.ReadValue());
                if (toolClickFrame == Time.frameCount) { } // F55: the click went to a tool or the settings box (CrewToolsFrame)
                else if (panelGroup != PanelGroup.None && panelHand == null) PanelFrame(mouse.leftButton.isPressed, Vector3.positiveInfinity, ray); // 2026-11-13: a picture row on the mouse
                else if (SketchbookHeld || takingSketchbook) { } // SketchbookFrame owns the drag and release.
                else if (carriedCard != null || FrameBusy) { } // F56: the card or the frame in the mouse's hand
                else if (mouse.leftButton.wasPressedThisFrame && (ShapeCardPinch(null, Vector3.positiveInfinity, ray) || ShowFramePinch(null, Vector3.positiveInfinity, ray))) { } // F56
                else if (mouse.leftButton.wasPressedThisFrame && SketchbookPinch(null, Vector3.positiveInfinity, ray)) { }
                else if (NotebookHeld) { UpdateNotebookGrab(); }
                else if (mouse.leftButton.wasPressedThisFrame && CardSkipPinch(Vector3.positiveInfinity, ray)) { } // F77: the lesson card's Skip
                else if (mouse.leftButton.wasPressedThisFrame && RePickPinch(Vector3.positiveInfinity, ray)) { } // F81: Change sketch
                else if (mouse.leftButton.wasPressedThisFrame && PlatePagerPinch(Vector3.positiveInfinity, ray)) { } // F94: < > on the stand
                else if (mouse.leftButton.wasPressedThisFrame && QuestionPinch(Vector3.positiveInfinity, ray)) { } // F44: a reply pill
                else if (mouse.leftButton.wasPressedThisFrame && StickerPinch(Vector3.positiveInfinity, ray)) { } // F47: a sticker's note
                else if (mouse.leftButton.wasPressedThisFrame && WallButtonPinch(null, Vector3.positiveInfinity, ray)) { } // F72: Rewrite, a picture's or card's x
                else if (EditorTools(mouse, ray)) { }
                else if (EditorDraft(mouse, ray, camera)) { } // F40/F39: flags and v2's size knobs
                else if (EditorSizeBox(mouse, ray)) { }       // F75: the size box's knobs
                else if (mouseHolding)
                {
                    if (mouse.leftButton.isPressed && mousePlane.Raycast(ray, out float d)) Hold(ray.GetPoint(d), Quaternion.identity);
                    else if (!mouse.leftButton.isPressed) LetGo();
                }
                else if (mouseTape)
                {
                    if (!mouse.leftButton.isPressed)
                    {
                        mouseTape = false;
                        var plane = new Plane(Vector3.up, mouseFrom);
                        if (plane.Raycast(ray, out float d) && Vector3.Distance(mouseFrom, ray.GetPoint(d)) > .01f)
                        {
                            Tape.SetMeasured(mouseFrom, ray.GetPoint(d));
                            SetMeasure(Tape.Length * 1000f, false);
                        }
                    }
                }
                else if (mouse.leftButton.wasPressedThisFrame && NotebookPinch(Vector3.positiveInfinity, ray)) { }
                else if (mouse.leftButton.wasPressedThisFrame)
                {
                    if (MadeOnRay(ray, out float madeDistance) is Transform clickedThing && (clickedThing != Product || buildFrom < 0f))
                    {
                        Vector3 point = ray.GetPoint(madeDistance);
                        mousePlane = new Plane(-camera.transform.forward, point);
                        held = clickedThing;
                        heldBody = clickedThing.GetComponent<Rigidbody>();
                        heldFar = -1f;
                        holdOffset = clickedThing.position - point;
                        holdTurn = clickedThing.rotation;
                        mouseHolding = true;
                        if (heldBody != null) heldBody.isKinematic = true;
                        heldPath.Clear();
                        heldTimes.Clear();
                    }
                    else if (Current == Step.Measuring)
                    {
                        if (Physics.Raycast(ray, out var deskHit, 4f, arrival.handoff.meeting.environment.GeometryMask)) { mouseFrom = deskHit.point; mouseTape = true; }
                    }
                    else if (PinNear(Vector3.positiveInfinity, ray, out var clickedView) is Pin clickedPin) UsePin(clickedPin, clickedView);
                    else
                    {
                        var idea = ChoiceOnRay(ray, out _);
                        if (idea != null) PickIdea(idea);
                        else if (!PanelPinch(null, Vector3.positiveInfinity, ray)) // a click picks, a drag carries the row
                        {
                            var picture = PictureOnRay(ray, out _);
                            if (picture != null && Current == Step.Looks) PickLook(picture);
                        }
                    }
                }
            }
            var keys = UnityEngine.InputSystem.Keyboard.current;
            if (keys == null) return;
            if (keys.enterKey.wasPressedThisFrame && Current == Step.Measuring) { if (!HasMeasure && Hint > 0f) SetMeasure(Hint, true); ConfirmMeasure(); }
            if (keys.pKey.wasPressedThisFrame) Print();
            if (keys.nKey.wasPressedThisFrame) { if (NotebookOpen) CloseNotebook(); else OpenNotebook(); }
            if (keys.vKey.wasPressedThisFrame) OnVoice("Compare them, which one is best?");
            if (NotebookOpen && keys.rightArrowKey.wasPressedThisFrame) FlipNotebook(1);
            if (NotebookOpen && keys.leftArrowKey.wasPressedThisFrame) FlipNotebook(-1);
            if (keys.lKey.wasPressedThisFrame) StartCoroutine(NameThings());
            if (keys.gKey.wasPressedThisFrame) StartCoroutine(Suggest());
        }
#endif

        private void LateUpdate()
        {
            if (targetTag != null) targetTag.rotation = SpeechBubbleDock.FacingAt(targetTag.position);
            if (!Begun)
            {
                // Spotting the object before the session starts: Spark can still talk.
                if (cast.Count > 0 && Voice != null) SpeakNext();
                if (captioned != null && Time.time > captionUntil && Voice != null && !Voice.IsSpeaking && !Voice.Loading && lines.Count == 0) HideCaption();
                try { CrewToolsLate(); } catch (Exception exception) { LogStepError("crew tools late", exception); } // F55: the introduction's tools
                return;
            }
            float blend = 1f - Mathf.Exp(-Time.deltaTime * 8f);
            // The row stays where it was placed (2026-11-13), except while the crew walks around to vote.
            if (Current == Step.Looks && Look == null) KeepRowOverBubble();
            PanelGuardFrame();        // 2026-11-13: the row and the review pictures step back from speech cards they cover
            ReviewPanelsFrame();
            FocusLook();
            var chosenNow = ChosenAtEyes ? ChosenPicture : null;
            foreach (var p in Looks)
            {
                if (p.root == null) continue;
                Vector3 step = Vector3.zero;
                if (p == chosenNow)
                {
                    // F102: steps back behind a speech card it covers; away while the old/new picture pair is up.
                    step = PushDir(p.home) * chosenPush;
                    float size = revisionPicture != null ? .001f : ChosenScale;
                    if (!Mathf.Approximately(p.root.localScale.x, size)) p.root.localScale = Vector3.one * Mathf.MoveTowards(p.root.localScale.x, size, Time.deltaTime * 4f);
                }
                p.root.position = Vector3.Lerp(p.root.position, p.home + step + HighlightNudge(p.root), blend); // F80: the talked-about one comes forward
                p.root.rotation = Current == Step.Looks && Look == null ? SpeechBubbleDock.FacingAt(RowCenter) : SpeechBubbleDock.FacingAt(p.root.position);
            }
            foreach (var c in Ideas)
            {
                c.root.position = Vector3.Lerp(c.root.position, c.home + Vector3.up * Mathf.Sin(Time.time * 2.4f + c.home.x * 9f) * .004f, blend);
                c.root.rotation = SpeechBubbleDock.FacingAt(c.root.position);
            }
            // Maker builds it layer by layer.
            if (Product != null && buildFrom >= 0f)
                Product.localScale = new Vector3(1f, Mathf.Clamp(Mathf.SmoothStep(.02f, 1f, (Time.time - buildFrom) / 1.5f), .02f, 1f), 1f);
            if (Sketching && cast.TryGetValue(Crew.Maker, out var drawing)) PlacePad(drawing, blend);
            // Fixer works with a little wrench beside it while it checks the plan.
            if (wrench != null && cast.TryGetValue(Crew.Fixer, out var fixer))
            {
                wrench.gameObject.SetActive(Working(Crew.Fixer) && Current == Step.Making);
                if (wrench.gameObject.activeSelf)
                {
                    Vector3 toward = HasSpot ? Vector3.ProjectOnPlane(MugSpot - fixer.transform.position, Vector3.up).normalized : Vector3.forward;
                    Vector3 at = fixer.transform.position + Vector3.up * .1f + toward * .06f;
                    wrench.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at) * Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 14f) * 28f));
                }
            }
            if (tapeLabelCard != null && tapeLabelCard.gameObject.activeSelf) tapeLabelCard.rotation = SpeechBubbleDock.FacingAt(tapeLabelCard.position);
            // Fixer's gap label goes after a while, or once the made thing is picked up.
            if (gapCard != null && gapCard.gameObject.activeSelf && (Time.time > gapUntil || (held != null && held == Product))) gapCard.gameObject.SetActive(false);
            if (gapCard != null && gapCard.gameObject.activeSelf) gapCard.rotation = SpeechBubbleDock.FacingAt(gapCard.position);
            foreach (var t in Tags) if (t != null) t.rotation = SpeechBubbleDock.FacingAt(t.position);
            try { HandOverLate(); } catch (Exception exception) { LogStepError("hand over late", exception); } // F66: the tools the crew carries
            foreach (var pair in tags)
            {
                if (pair.Value == null || !cast.TryGetValue(pair.Key, out var c) || c == null) continue;
                // F81: just over the head (the speech card stands above it, WindupsArrival.NameTagTop); it was a fixed 21 cm
                // over the feet, inside the card.
                var tagBody = WindupBody.Of(c);
                Vector3 at = tagBody != null ? new Vector3(c.transform.position.x, tagBody.HeadTop.y + CrewTagHeight * .5f + .006f, c.transform.position.z)
                    : c.transform.position + Vector3.up * .21f;
                pair.Value.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at) * Quaternion.Euler(0f, 0f, Working(pair.Key) ? Mathf.Sin(Time.time * 9f) * 8f : 0f));
            }
            Transform look = CrewWatchingProduct ? Product // 2026-11-13: only for a while after a make (MakerSession.Make3D.cs)
                : Current == Step.Looks && Looks.Count > 0 && Looks[Looks.Count / 2].root != null ? Looks[Looks.Count / 2].root
                : Current == Step.Measuring && tapeBar.gameObject.activeSelf ? tapeBar : Head;
            foreach (var pair in cast)
                if (pair.Value != null && !(pair.Key == Crew.Spark && SparkWalking))
                    pair.Value.lookTarget = StickerNoteLook(pair.Key) ?? HighlightLook(pair.Value) ?? (pair.Key == Crew.Maker && Sketching ? sketchPad : look); // F47: its open sticker note; F76: the picture it talks about
            InputsLateFrame(blend);   // F33: the show-me circle, Maker offering its sketchbook (MakerSession.Inputs.cs)
            SculptLateFrame();        // F33: clay, brush and the made thing while Meshy works (MakerSession.Sculpt.cs)
            EndingFrame();
            if (ending == EndingState.Working && !FinishReady) CrewFrame();
            StickerNotesFrame();      // F47: fingertips on the look stickers, the open note (MakerSession.StickerNotes.cs)
            DraftFrame();             // F40: v2, its flags and its size lines
            NotebookFrame(blend);
            MagnifierFrame();
            try { CrewToolsLate(); } catch (Exception exception) { LogStepError("crew tools late", exception); } // F55: Maker's book, Fixer's gear
            NotebookSnapshotFrame();
            SpeakNext();
            QuestionFrame();
            ReviewFrame();          // F50
            TutorialCaptionFrame();
            CalmFrame(); // F76: reading, the picture talked about, "look at me!"
            // F44: the open question's reply pills (MakerSession.Questions.cs)
            if (captioned != null && Time.time > captionUntil && !Voice.IsSpeaking && !Voice.Loading && lines.Count == 0 && (!Calm || CaptionRead())) HideCaption();
        }

        // ------------------------------------------------------------ speech (one line after another)

        // The crew's voice can be switched off (WindupsArrival.crewVoice): their speech cards still show,
        // each for as long as it takes to read.
        private bool Voiced => arrival != null && arrival.crewVoice;

        private void Say(Crew crew, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            lines.Enqueue((crew, text));
            Talk.Add(MakerRoles.Name(crew) + ": " + text);
            LastSay = text;
            LastSpeaker = crew;
            TrimQueue(); // F76: at most QueueKeep lines wait
        }

        // The person has moved on: lines still waiting from the step before are dropped (the one being said finishes).
        private void Hush() { lines.Clear(); queuedQuestions.Clear(); lineFocus.Clear(); }

        private void SpeakNext()
        {
            if (collectingCreation || lines.Count == 0 || Voice.IsSpeaking || Voice.Loading) return;
            bool now = RightAwayNext; // 2026-11-15: a question about what the person just did (AskNow)
            bool brisk = BriskNext;   // F92: that, or a teammate showing the ways to do something
            if (Time.time < nextLineAt && !now) return;
            if (Calm && !CaptionRead() && !brisk) return; // F76: the bubble on show is read first
            if (CurrentLesson != null && lines.Peek().crew != CurrentLesson.who) return;
            var (crew, text) = lines.Dequeue();
            if (now) rightAway = null;
            lastLineAt = Time.time;
            float read = Calm ? ReadSeconds(text) : 1.4f + MakerRoles.Words(text) * .28f;
            Caption(crew, text, read + 1f);
            LineShown(crew, text); // F44: a question opens its reply pills above the card
            LineFocus(crew, text, read + 1f); // F76: the picture it talks about grows, the bubble's border glows
            // Without a voice, the next card waits until this one has been read. F92: on the beat while the ways are
            // being shown, but never less than this line's own reading time (so a line is not cut off mid-sentence).
            nextLineAt = Time.time + (Calm ? (brisk ? Mathf.Min(read, TutorialBeat) : LineGap) : Scripted ? read * .5f : Voiced ? .4f : read);
            if (!cast.TryGetValue(crew, out var c)) return;
            WindupBody.Express(c, text, read + 1f);
            Voice.Enabled = !Scripted && Voiced;
            Voice.Speak(MakerRoles.VoiceOf(crew), c.transform, text, length => { if (length > 0f) captionUntil = Mathf.Max(captionUntil, Time.time + length + 1f); });
        }

        private void Caption(Crew crew, string text, float seconds)
        {
            if (!cast.TryGetValue(crew, out var character) || character == null) return;
            if (captioned != null && captioned != character) HideCaption();
            captioned = character;
            captionUntil = Time.time + seconds;
            CaptionShown(crew, character, text); // F76: reading time, mumble
            var bubble = character.authoredBubble;
            if (bubble == null) return;
            // F52: the words go in before the card shows: a card shown first popped up with its old (or, the first
            // time, its scene) text for a moment - e.g. a leftover hat question "What is the next step?".
            bubble.SetText(MakerRoles.Name(crew), text, "");
            bubble.title.text = text;
            bubble.gameObject.SetActive(true);
            if (!bubble.IsHeld && bubble.startingDock != null && bubble.Owner != bubble.startingDock) bubble.startingDock.Attach(bubble);
        }

        private void HideCaption()
        {
            // (F44: an open question's card stays up until it is answered.)
            if (captioned != null && captioned.authoredBubble != null && !captioned.authoredBubble.IsHeld && !Pinned(captioned) && !LessonPinned(captioned))
                HideBubble(captioned.authoredBubble); // F76: shrinks away (calm talk)
            if (highlightSpeaker != null && highlightSpeaker == captioned) EndHighlight();
            captioned = null;
        }

        private void Cue(Crew crew, string cue) { if (cast.TryGetValue(crew, out var c) && c != null) c.SetCue(cue); }

        // The modelled body works in a loop (true) / finishes with Work_Done (true); false = no model.
        private bool BodyWork(Crew crew, string loop)
        {
            var body = cast.TryGetValue(crew, out var c) ? WindupBody.Of(c) : null;
            if (body == null) return false;
            body.StartWork(loop);
            return true;
        }

        private bool BodyDone(Crew crew)
        {
            var body = cast.TryGetValue(crew, out var c) ? WindupBody.Of(c) : null;
            if (body == null) return false;
            body.StopWork(true);
            return true;
        }
        private IEnumerator After(float seconds, Action action) { yield return new WaitForSeconds(seconds); action(); }

        // ------------------------------------------------------------ visuals

        private void BuildTools()
        {
            BuildTapeDim(); // F39: the tape is a blueprint dimension line (MakerSession.Dims.cs)
            BuildSettleBar();
            ShowTape(false);
            gapCard = PaperTag("Gap", .09f, .026f, new Color(.86f, 1f, .88f), out gapLabel, 12f); // F77: a paper label, not a pill
            gapCard.gameObject.SetActive(false);
            BuildMagnifier();
            focus = new GameObject("Spark Focus").transform;
            focus.SetParent(transform, false);
            // Maker's spiral sketchbook (pages are added while it draws) and its pencil.
            var padMesh = ToyMeshes.RoundedSlab(PadW, PadH, .004f, .004f, new Color(.98f, .97f, .93f), new Color(.36f, .45f, .62f), new Color(.36f, .45f, .62f), "SketchBook");
            owned.Add(padMesh);
            sketchPad = Part("Sketch Book", padMesh);
            pageMesh = ToyMeshes.RoundedSlab(PadW - .005f, PadH - .009f, .0025f, .0006f, new Color(1f, .995f, .975f), new Color(.9f, .89f, .86f), new Color(.95f, .94f, .92f), "SketchPage");
            owned.Add(pageMesh);
            var ringMesh = ToyMeshes.Disc(.0024f, .0011f, new Color(.72f, .74f, .78f), new Color(.55f, .57f, .6f), "SpiralRing");
            owned.Add(ringMesh);
            for (int i = 0; i < 8; i++)
            {
                var spiral = Part("Spiral", ringMesh);
                spiral.SetParent(sketchPad, false);
                spiral.localPosition = new Vector3(-PadW * .5f + .007f + i * (PadW - .014f) / 7f, PadH * .5f - .0025f, -.0006f);
                spiral.localRotation = Quaternion.Euler(0f, 90f, 0f);
            }
            // The pencil lies along +z from its point (at the origin).
            pencil = new GameObject("Pencil").transform;
            pencil.SetParent(transform, false);
            void Stick(string name, float width, float from, float to, Color color)
            {
                var mesh = ToyMeshes.RoundedSlab(width, to - from, width * .35f, width, color, Color.Lerp(color, Color.black, .18f), color, name);
                owned.Add(mesh);
                var part = Part(name, mesh);
                part.SetParent(pencil, false);
                part.localRotation = Quaternion.Euler(90f, 0f, 0f);
                part.localPosition = new Vector3(0f, width * .5f, (from + to) * .5f);
            }
            Stick("Lead", .0011f, 0f, .0022f, new Color(.2f, .2f, .23f));
            Stick("Wood Tip", .0022f, .0014f, .0042f, new Color(.93f, .78f, .58f));
            Stick("Wood", .0034f, .0036f, .0064f, new Color(.93f, .78f, .58f));
            Stick("Body", .0044f, .006f, .036f, new Color(1f, .8f, .2f));
            Stick("Band", .0046f, .0358f, .0386f, new Color(.78f, .8f, .84f));
            Stick("Eraser", .0044f, .0384f, .0426f, new Color(.98f, .6f, .66f));
            pencil.gameObject.SetActive(false);
            sketchPad.gameObject.SetActive(false);
            // Fixer's wrench: a handle and an open ring.
            wrench = new GameObject("Wrench").transform;
            wrench.SetParent(transform, false);
            var gripMesh = ToyMeshes.RoundedSlab(.007f, .032f, .0034f, .004f, new Color(.62f, .66f, .7f), new Color(.45f, .48f, .52f), new Color(.62f, .66f, .7f), "WrenchGrip");
            owned.Add(gripMesh);
            var grip = Part("Grip", gripMesh);
            grip.SetParent(wrench, false);
            grip.localPosition = new Vector3(0f, -.012f, 0f);
            var jawMesh = ToyMeshes.Torus(.3f, "WrenchJaw");
            Paint(jawMesh, new Color(.62f, .66f, .7f));
            owned.Add(jawMesh);
            var jaw = Part("Jaw", jawMesh);
            jaw.SetParent(wrench, false);
            jaw.localPosition = new Vector3(0f, .009f, 0f);
            jaw.localScale = Vector3.one * .008f;
            wrench.gameObject.SetActive(false);
        }

        private Transform Part(string name, Mesh mesh)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(transform, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        private void ShowTape(bool on)
        {
            if (tapeBar == null) return;
            if (!on) { tapePinnedUntil = 0f; tickCm = -1; }
            ShowDim(tapeDim, on);
        }

        // F39: the blueprint line between the ends (snapped by SnapTapeEnds), its rings and dashes.
        private void UpdateTapeVisual()
        {
            Vector3 a = Tape.A, b = Tape.B;
            float length = Mathf.Max(.001f, Vector3.Distance(a, b));
            float mm = !freeTape && !tapeDemo && HasMeasure && Tape.State == HandTape.Mode.Measured ? Measured : Mathf.Round(length * 2000f) / 2f;
            DrawDim(tapeDim, a, b, TapeText(mm), 0, TapeSnappedA, TapeSnappedB,
                    TapeSnappedA && tapeExtA != Vector3.zero ? tapeExtA : (Vector3?)null, TapeSnappedB && tapeExtB != Vector3.zero ? tapeExtB : (Vector3?)null);
            if (Tape.State == HandTape.Mode.TwoHands) TapeTicks(length);
        }

        private Picture MakePicture(float size, string label)
        {
            var picture = new Picture { size = size };
            var root = new GameObject("Picture " + label).transform;
            root.SetParent(transform, false);
            var slab = ToyMeshes.RoundedSlab(size + .016f, size + .034f, .01f, .004f, Color.white, new Color(.85f, .85f, .88f), Color.white, "PictureCard");
            owned.Add(slab);
            var card = new GameObject("Card", typeof(MeshFilter), typeof(MeshRenderer));
            card.transform.SetParent(root, false);
            card.transform.localPosition = new Vector3(0f, -.008f, 0f);
            card.GetComponent<MeshFilter>().sharedMesh = slab;
            card.GetComponent<MeshRenderer>().sharedMaterial = material;
            var canvas = new GameObject("Picture", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(root, false);
            rect.sizeDelta = new Vector2((size + .016f) * 1000f, (size + .034f) * 1000f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, -.008f, -.0025f);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var image = new GameObject("Image", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            image.rectTransform.SetParent(rect, false);
            image.rectTransform.anchorMin = image.rectTransform.anchorMax = new Vector2(.5f, 1f);
            image.rectTransform.pivot = new Vector2(.5f, 1f);
            image.rectTransform.anchoredPosition = new Vector2(0f, -8f);
            image.rectTransform.sizeDelta = new Vector2(size * 1000f, size * 1000f);
            image.color = new Color(.95f, .94f, .9f);
            image.raycastTarget = false;
            picture.image = image;
            picture.label = HatIdeaCard.Label(rect, font, 6f, size * 1000f + 9f, size * 1000f + 4f, 18f, 7f, 11f, new Color(.15f, .15f, .18f), FontStyles.Bold, TextAlignmentOptions.Center);
            picture.label.text = label;
            picture.root = root;
            return picture;
        }

        private void ClearLooks()
        {
            // F63: stickers still flying to these pictures (not yet parented to them) go with them.
            foreach (var s in Stickers) if (s.root != null && s.root.parent == transform && Looks.Contains(s.picture)) Destroy(s.root.gameObject);
            foreach (var p in Looks) if (p.root != null) Destroy(p.root.gameObject);
            Looks.Clear();
            LooksShown = false;
            Stickers.Clear();
            ClearTabs();
        }

        private void ClearIdeas()
        {
            foreach (var c in Ideas) if (c.root != null) Destroy(c.root.gameObject);
            Ideas.Clear();
        }

        private Transform NameTag(Crew crew)
        {
            var tag = Panel(MakerRoles.Name(crew) + " Tag", .07f, CrewTagHeight, Color.Lerp(MakerRoles.Color(crew), Color.white, .8f), out var text, 11f);
            text.text = "<b>" + MakerRoles.Name(crew) + "</b>";
            text.color = Color.Lerp(MakerRoles.Color(crew), Color.black, .35f);
            return tag;
        }

        // A small rounded card with one text block, in metres; text size in canvas millimetres.
        private Transform Panel(string name, float width, float height, Color color, out TextMeshProUGUI text, float size)
        {
            var root = new GameObject(name).transform;
            root.SetParent(transform, false);
            var slab = ToyMeshes.RoundedSlab(width, height, Mathf.Min(height * .45f, .01f), .003f, color, Color.Lerp(color, Color.black, .12f), color, name);
            owned.Add(slab);
            var go = new GameObject("Card", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(root, false);
            go.GetComponent<MeshFilter>().sharedMesh = slab;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            var canvas = new GameObject("Text", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(root, false);
            rect.sizeDelta = new Vector2(width * 1000f, height * 1000f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, 0f, -.0022f);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            text = HatIdeaCard.Label(rect, font, 3f, 2f, width * 1000f - 6f, height * 1000f - 4f, size * .6f, size, new Color(.15f, .15f, .18f), FontStyles.Normal, TextAlignmentOptions.Center);
            text.richText = true;
            return root;
        }

        // ------------------------------------------------------------ test entries

        public void SimulatePinch(Vector3 point) => OnPinch(null, point, null);
        public void SimulateTape(Vector3 a, Vector3 b) { Tape.SetMeasured(a, b); SetMeasure(Tape.Length * 1000f, false); }
        public void SimulateDrop(Vector3 at)
        {
            if (Product == null) return;
            SimulateCarry(Product, at);
        }

        // Tests: pick a made thing up, carry it to `at` and let go there.
        public void SimulateCarry(Transform thing, Vector3 at)
        {
            if (thing == null) return;
            Grab(null, thing.position, thing);
            mouseHolding = true;
            // F38: carried there in the hand's steps, so it stops at the room on the way like a real carry.
            for (int i = 1; i <= 12 && held == thing; i++) Hold(Vector3.Lerp(thing.position, at, i / 12f), Quaternion.identity);
            heldPath.Clear();
            heldTimes.Clear();
            LetGo();
        }

        // Tests: what a made thing is remembered as (null when it is not).
        public string MadeName(Transform thing) => thing != null && madeNames.TryGetValue(thing, out var name) ? name : null;
    }
}
