using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction.Input;
using RoomCapsule;
using UnityEngine;
using UnityEngine.UI;
using Windups.BubbleLab;

namespace Windups.Handoff
{
    public sealed class WindupsArrival : MonoBehaviour
    {
        public MainBubbleHandoff handoff;
        public WindupCharacter guide;
        public WindupsMicrophone microphone;
        public GameObject microphoneCube;
        public GameObject microphoneButton;
        public Material topicNoteMaterial;
        // Optional toy-style push-to-talk puck; null keeps the flat button.
        public Material microphonePuckMaterial;
        public Texture2D microphoneIcon;
        public string GuideMessage => guide.authoredBubble.gameObject.activeInHierarchy ? guide.authoredBubble.title.text : "";
        public TransferableSpeechBubble TopicNote { get; private set; }
        // The designed post-it look and pad for the topic note (WindupsPostIt).
        private WindupsPostIt postIt;
        public WindupsPostIt PostIt => postIt;
        // F28: Thinkmade launch intro (v4): the title plate and the crew peeking over it, then hopping off.
        public bool launchIntro = true;
        private WindupsTitle title;
        public WindupsTitle Title => title;
        public bool IntroActive => maker && launchIntro;
        public enum Phase { Entering, WaitingForClaps, Gathering, Introducing, OfferingTopic, WaitingForTopic, ReviewingTopic, Meeting }
        public Phase CurrentPhase { get; private set; }

        public Renderer TopicRing { get; private set; }
        public float RingRadius => ringRadius;
        [Tooltip("How the topic ring in the middle of the crew looks: the slim ring, or a hand-drawn circle that inks itself when a note settles in it (2026-11-12, draft A).")]
        public RingLook ringLook = RingLook.Doodle;
        private TopicRingLook ringView;
        // Height above the ring's centre where a standing note's bottom rests (the look's top).
        public float RingRest => ringView != null ? ringView.Rest : .012f;
        public GatherFormation Formation => formation;
        public bool FormationOnFurniture => formation != null && formation.OnFurniture;
        public Vector3 FormationCenter => formation != null ? formation.Center : Vector3.zero;
        public int IntroducedCount { get; private set; }
        public string ConfirmedTopic => CurrentPhase == Phase.Meeting ? topicDraft : "";
        private float ringRadius = .32f;
        private GatherFormation formation;
        private readonly Dictionary<string, CharacterIntroCard> introCards = new Dictionary<string, CharacterIntroCard>();
        private float gatherStartedAt;
        // Height kept free right above each head for the role name tag.
        public const float TagLift = .06f;
        [Header("Meeting layout (applies at the next gathering; card height applies live)")]
        [Tooltip("Topic ring radius on a desk, in meters. Smaller sizes are tried if the desk is too small.")]
        [Range(.08f, .3f)] public float deskRingRadius = .16f;
        [Tooltip("Gap between the ring and the characters on a desk, in meters. Larger = more space between characters.")]
        [Range(.06f, .4f)] public float deskSeatGap = .26f;
        [Tooltip("Topic ring radius when the meeting is on the floor, in meters.")]
        [Range(.12f, .4f)] public float floorRingRadius = .24f;
        [Tooltip("Gap between the ring and the characters on the floor, in meters.")]
        [Range(.1f, .5f)] public float floorSeatGap = .3f;
        [Tooltip("Smallest allowed distance between two characters (centre to centre), in meters. Characters are 8 cm wide.")]
        [Range(.06f, .25f)] public float minCharacterSpacing = .14f;
        [Tooltip("How wide the arc of characters opens, in degrees.")]
        [Range(90f, 210f)] public float arcSpan = 170f;
        [Tooltip("Height of each speech card above its character's head, in meters.")]
        [Range(0f, .2f)] public float cardLift = .04f;
        [Tooltip("F26: make what your space needs (a coaster for the user's own mug). Spark, Maker and Fixer; replaces the topic note.")]
        public bool maker = false;
        [Tooltip("Thinkmade: the crew's voice (text-to-speech). Off for now; their speech cards stay (2026-11-11).")]
        public bool crewVoice = false;
        [Tooltip("F48: live speech is read by the Crew Director (intent, who answers, named app tools) instead of the keyword router. Off until checked on the headset.")]
        public bool crewDirector = false;
        private MakerSession makerSession;
        public MakerSession Maker => makerSession;
        public bool MakerActive => maker && makerSession != null && makerSession.Begun;
        // Characters that sit out the maker crew (hidden at startup).
        public int BenchedCount { get; private set; }
        private const float GatherPatience = 15f;
        private float ringShownAt = -10f;
        private bool micMoving;
        private Material ringMaterial;
        private string topicDraft;
        private Material noteMaterial;
        private bool dockWhenReleased;
        private readonly DoubleClap clap = new DoubleClap { Required = 1 };
        // F29 idea wall: the topic note also serves as the top sheet of the wall pad (maker, coming back).
        public enum PadNoteState { None, OnPad, Peeled, Written }
        public PadNoteState PadNote { get; private set; }
        // F72: with Maker the confirmed note goes up on the wall (no ring); held, it goes when it is let go.
        private bool postWhenReleased;
        public string TopicDraft => topicDraft;
        private bool NoteOnWall => maker && makerSession != null && MakerSession.NoteOnWall;
        private readonly Dictionary<CapsulePetController, Vector3> homes = new Dictionary<CapsulePetController, Vector3>();
        private readonly Dictionary<CapsulePetController, float> nextWalk = new Dictionary<CapsulePetController, float>();
        private Hand[] hands;
        private bool paused;
        private Vector3 gatherHead;
        private Vector3 gatherForward;
        private List<Vector3>[] gatherCandidates;
        private readonly Dictionary<CapsulePetController, Vector3> seats = new Dictionary<CapsulePetController, Vector3>();
        private float nextGatherAttempt;
        private bool guideDismissed;
        private TangibleBubble topicPaper;
        private Graphic topicTapSurface;
        private PressAndHoldMicrophone pushToTalk;
        private bool topicTaken;
        private float firstTopicTap = float.NegativeInfinity;

        private void Awake()
        {
            // F60: the mic's poke/ray events: a press while a hand pinches away from the mic (the ray of a hand working
            // elsewhere) or while a hand holds something of the session is not a press.
            PressAndHoldMicrophone.Blocked = MicPointerBlocked;
            if (maker) StartCoroutine(WindupsNetwork.Check()); // F78: no internet = airplane mode (collect, MakerArrives)
            if (maker) BenchExtraCharacters();
            foreach (var dock in handoff.docks) dock.character.movement.RoamingEnabled = false;
            // The intro brings the crew in itself: keep them out of the room until the title shows them.
            if (IntroActive) foreach (var dock in handoff.docks) dock.character.movement.HoldForIntro = true;
            foreach (var bubble in handoff.bubbles) bubble.gameObject.SetActive(false);
            if (microphone == null) microphone = FindFirstObjectByType<WindupsMicrophone>(FindObjectsInactive.Include);
            if (microphone != null)
            {
                microphone.TranscriptUpdated += HandleTranscriptUpdate;
                microphone.TranscriptReady += HandleTranscript;
            }
            if (microphoneCube != null) microphoneCube.SetActive(false);
            if (microphoneButton != null) microphoneButton.SetActive(false);
            var guideBubble = guide.authoredBubble;
            var guideCanvas = guideBubble.GetComponentInChildren<Canvas>(true);
            var guideTap = guideCanvas?.transform.Find("TopicTapSurface")?.GetComponent<Graphic>();
            if (guideTap != null) guideTap.raycastTarget = false;
            TopicNote = Instantiate(guideBubble, transform);
            guideBubble.GetComponent<SpeechBubblePresentation>().UseGuideCaption();
            TopicNote.name = "Arrival Topic Sticky Note";
            TopicNote.startingDock = null;
            TopicNote.initialMessage = "";
            TopicNote.Released += OnTopicReleased;
            topicPaper = TopicNote.GetComponent<TangibleBubble>();
            postIt = TopicNote.gameObject.AddComponent<WindupsPostIt>();
            topicTapSurface = TopicNote.GetComponentInChildren<Canvas>(true)
                ?.transform.Find("TopicTapSurface")?.GetComponent<Graphic>();
            if (microphone != null && microphoneButton != null)
            {
                var target = microphoneButton.transform.Find("MIC");
                pushToTalk = target != null ? target.GetComponent<PressAndHoldMicrophone>() : null;
                var guideLook = guideBubble.GetComponent<SpeechBubblePresentation>();
            microphone.ConfigureArrivalButton(target, microphoneIcon, microphonePuckMaterial, guideBubble.title != null ? guideBubble.title.font : null,
                guideLook != null ? guideLook.toyFont : null);
            }
        }

        // Maker: only Spark (Creativity), Maker (Decisions) and Fixer (Caution) take part; the other
        // three capsules and their cards are switched off before anything uses them.
        private void BenchExtraCharacters()
        {
            var keep = new List<SpeechBubbleDock>();
            foreach (var dock in handoff.docks)
            {
                if (dock.character != null && InCast(dock.character.characterName)) keep.Add(dock);
                else if (dock.character != null) { dock.character.gameObject.SetActive(false); BenchedCount++; }
            }
            if (keep.Count == 0) return;
            var bubbles = new List<TransferableSpeechBubble>();
            foreach (var bubble in handoff.bubbles)
            {
                if (bubble != null && keep.Contains(bubble.startingDock)) bubbles.Add(bubble);
                else if (bubble != null) bubble.gameObject.SetActive(false);
            }
            handoff.docks = keep.ToArray();
            handoff.bubbles = bubbles.ToArray();
            var cast = new List<WindupCharacter>();
            foreach (var character in handoff.meeting.characters) if (character != null && InCast(character.characterName)) cast.Add(character);
            handoff.meeting.characters = cast.ToArray();
            // Maker (F26): Maker (Decisions) leads, so it is the guide who introduces the crew and offers the note.
            if (maker)
                foreach (var dock in handoff.docks)
                    if (dock.character != null && dock.character.characterName == MakerRoles.CharacterFor(Crew.Maker)) guide = dock.character;
        }

        // Spark (Creativity), Maker (Decisions), Fixer (Caution).
        private bool InCast(string character) => MakerRoles.InCast(character);

        // F52 (2026-11-12, the user: "in v4 take out stretching and crumpling the speech bubbles"): in the maker session
        // the crew's speech cards are only speech: their grab interactables are off, so a hand passing them never
        // turns one into the BubbleLab paper to stretch or crumple. The topic note and the wall pad's note stay grabbable.
        // 2026-11-13: something switched them back on (MakerSession's crew-grab switch also caught the cards hanging
        // under the characters), so the cards could be stretched and crumpled again. Now every speech card in the
        // meeting is checked twice a second and anything switched back on goes off again (RelockedSpeechCards).
        public int LockedSpeechCards { get; private set; }
        public int RelockedSpeechCards { get; private set; }
        private readonly List<MonoBehaviour> lockedCardParts = new List<MonoBehaviour>();
        private float relockAt;
        private void LockSpeechCards()
        {
            lockedCardParts.Clear();
            foreach (var card in handoff.bubbles)
            {
                if (card == null || card == TopicNote) continue;
                foreach (var behaviour in card.GetComponentsInChildren<MonoBehaviour>(true))
                    if (behaviour is Oculus.Interaction.IInteractable)
                    {
                        lockedCardParts.Add(behaviour);
                        if (behaviour.enabled) { behaviour.enabled = false; LockedSpeechCards++; }
                    }
            }
        }

        private void KeepSpeechCardsLocked()
        {
            if (!maker || lockedCardParts.Count == 0 || Time.time < relockAt) return;
            relockAt = Time.time + .5f;
            for (int i = 0; i < lockedCardParts.Count; i++)
            {
                var part = lockedCardParts[i];
                if (part == null || !part.enabled) continue;
                // A card being held (an old grab that started before) is let go by switching its grab off too.
                part.enabled = false;
                RelockedSpeechCards++;
            }
        }

        private IEnumerator Start()
        {
            hands = FindObjectsByType<Hand>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            // Card gestures in the meeting (stickers, tap, hold, read, ring, hand over, basket, notes).
            if (!TryGetComponent<BubbleInteractions>(out var cardInteractions)) cardInteractions = gameObject.AddComponent<BubbleInteractions>();
            // The modelled team's room reactions: "shh", pointing.
            if (!TryGetComponent<WindupsGestures>(out var gestures)) gestures = gameObject.AddComponent<WindupsGestures>();
            gestures.arrival = this;
            cardInteractions.Initialize(this);
            // Pinch from afar (like the UI ray) for speech cards, the topic note and deck copies.
            if (!TryGetComponent<RemoteGrab>(out var remoteGrab)) remoteGrab = gameObject.AddComponent<RemoteGrab>();
            remoteGrab.arrival = this;
            // F51: a character let go over an open palm stands on it.
            if (!TryGetComponent<PalmPerch>(out var palmPerch)) palmPerch = gameObject.AddComponent<PalmPerch>();
            palmPerch.arrival = this;
            // The crew's spoken voice (text-to-speech); the maker session and gestures find it on this object.
            if (!TryGetComponent<StudioVoice>(out _)) gameObject.AddComponent<StudioVoice>();
            // Maker (F26): starts right after the team gathers.
            if (maker)
            {
                if (!TryGetComponent(out makerSession)) makerSession = gameObject.AddComponent<MakerSession>();
                makerSession.arrival = this;
            }
            while (!handoff.Ready) yield return null;
            if (maker) LockSpeechCards();
            foreach (var dock in handoff.docks)
            {
                var pet = dock.character.movement;
                nextWalk[pet] = Time.time + Random.Range(4f, 8f);
            }
            if (makerSession != null && !Roomless) yield return makerSession.RestoreBeforeTitle(); // F78: no room, nothing to put back
            if (IntroActive)
            {
                title = gameObject.AddComponent<WindupsTitle>();
                title.arrival = this;
                title.PlateMode = maker && Roomless; // F78: the sign stays as the crew's stage
                yield return title.Play();
                if (title.PlateMode)
                {
                    // F78: nothing to gather on: the crew on the sign goes straight on (no clap).
                    SpeechBubbleDock.FaceFrom(handoff.meeting.Head.position, guide.transform.position);
                    guide.authoredBubble.gameObject.SetActive(true);
                    yield return null;
                    CurrentPhase = Phase.Introducing;
                    Debug.Log("[Windups Arrival] Airplane mode: the crew stays on the sign.");
                    StartCoroutine(MakerArrives());
                    yield break;
                }
                var viewer = handoff.meeting.Head;
                SpeechBubbleDock.FaceFrom(viewer.position, guide.transform.position);
                if (ClapKnown) { ComeOverWithoutClap(); yield break; } // F82/F96: no clap after the first time
                guide.authoredBubble.gameObject.SetActive(true);
                yield return null;
                ShowGuide("Clap once to call us over.\nClap anytime, we'll come!");
                ClapCuesShown++;
                CurrentPhase = Phase.WaitingForClaps;
                Debug.Log("[Windups Arrival] Intro done; waiting for the clap.");
                yield break;
            }
            yield return new WaitForSeconds(2f);
            var head = handoff.meeting.Head;
            var guidePet = guide.movement;
            var room = handoff.meeting.environment;
            TryAssignSeat(guidePet, new ArrivalSeats(room, head.position, FlatForward(head)).Ranked(0, room), 1.8f);
            float deadline = Time.time + 10f;
            while (guidePet.State == CapsulePetController.MotionState.Walking && Time.time < deadline)
                yield return null;
            guidePet.StopWalking();
            seats.Clear();
            // Cards are world-locked: face them toward the user once, from where they are now.
            SpeechBubbleDock.FaceFrom(head.position, guide.transform.position);
            if (ClapKnown) { ComeOverWithoutClap(); yield break; } // F82/F96: no clap after the first time
            guide.authoredBubble.gameObject.SetActive(true);
            yield return null; // Initialize the original dock before setting the guide line.
            ShowGuide("Clap once to call everyone over.\nClap anytime, we'll come!");
            ClapCuesShown++;
            CurrentPhase = Phase.WaitingForClaps;
            Debug.Log("[Windups Arrival] Waiting for two claps.");
        }

        private void Update()
        {
            if (!handoff.Ready || paused || !(handoff.meeting.environment.IsReady || Roomless)) // F78: no room in airplane mode
            {
                clap.Reset();
                return;
            }
            KeepSpeechCardsLocked();
            // Name tags above the characters open/close their profiles at any phase.
            if (UpdateTagTaps()) return;
            TopicQuietFrame();
            // Review waits for the user: put the note in the ring or say "I'm done".
            // (No automatic confirmation; holding the mic again adds to the note.)
            if (CurrentPhase == Phase.ReviewingTopic) return;
            if (CurrentPhase >= Phase.Gathering)
            {
                // (F79: the maker crew's lift from the gathering on, not only once the session is active.)
                float lift = cardLift + (maker ? MakerSession.SpeechLift : 0f);
                foreach (var dock in handoff.docks)
                {
                    // F81 (Quest 2026-11-14: "Maker's bubble still overlaps Maker's name tag when the introduction moves on to
                    // Fixer - every bubble should sit a little above the name tag"): a speech card starts BubbleOverTag above
                    // the top of its character's name tag (the profile card's tag, the maker crew's tag), in 5 mm steps.
                    float want = lift;
                    float top = NameTagTop(dock);
                    if (top > float.MinValue && dock.anchor != null)
                        want = Mathf.Max(want, Mathf.Ceil((top + BubbleOverTag - dock.anchor.position.y) / .005f) * .005f);
                    // (up at once; down only by more than a bob of the head, so the card does not twitch)
                    if (want > dock.lift + .001f || want < dock.lift - .009f || (top == float.MinValue && !Mathf.Approximately(dock.lift, want))) dock.lift = want;
                }
            }
            if (CurrentPhase == Phase.Meeting)
            {
                if (PadNote == PadNoteState.OnPad && TopicNote.IsHeld) PeelPadNote();
#if UNITY_EDITOR
                if (PadNote == PadNoteState.OnPad && !UnityEngine.XR.XRSettings.isDeviceActive &&
                    UnityEngine.InputSystem.Mouse.current is { } padMouse && padMouse.leftButton.wasPressedThisFrame &&
                    Camera.main != null && TopicNote.Box.Raycast(Camera.main.ScreenPointToRay(padMouse.position.ReadValue()), out _, 8f))
                    PreviewPeelPadNote();
#endif
                if (dockWhenReleased && !TopicNote.IsHeld) DockTopic();
                // F29: clapping always calls the crew over, not only at the start.
                if (MakerActive && makerSession.CanCallOver && !callingOver && StrictClapHeard()) CallCrewOver();
                return;
            }
            if (CurrentPhase == Phase.Gathering)
            {
                UpdateGathering();
                return;
            }
            if (CurrentPhase == Phase.Introducing) return;
            if (CurrentPhase == Phase.OfferingTopic && TopicNote.IsHeld && !topicTaken)
            {
                TakeTopicNote();
                return;
            }
#if UNITY_EDITOR
            if (CurrentPhase == Phase.OfferingTopic && !UnityEngine.XR.XRSettings.isDeviceActive &&
                UnityEngine.InputSystem.Mouse.current is { } mouse && mouse.leftButton.wasPressedThisFrame &&
                Camera.main != null && TopicNote.Box.Raycast(Camera.main.ScreenPointToRay(mouse.position.ReadValue()), out _, 8f))
            {
                PreviewTakeTopicNote();
                return;
            }
#endif
            if (CurrentPhase == Phase.Entering)
            {
                WalkLocally();
                return;
            }
            if (CurrentPhase != Phase.WaitingForClaps) return;
            WalkLocally();
            foreach (var dock in handoff.docks)
                if (dock.character.movement.IsHeld) { clap.Reset(); return; }
            foreach (var bubble in handoff.bubbles)
                if (bubble.IsHeld) { clap.Reset(); return; }
#if UNITY_EDITOR
            if (!UnityEngine.XR.XRSettings.isDeviceActive)
            {
                if (UnityEngine.InputSystem.Keyboard.current?.cKey.wasPressedThisFrame == true)
                    PreviewClap(Time.unscaledTime);
                return;
            }
#endif
            // The first call uses the same strict clap as later calls: open hands that really clap,
            // not hands that happen to rest together.
            if (StrictClapHeard()) BeginGathering();
        }

        // A real clap (StrictClap), used for the first call and every later call-over: open hands
        // (fingertips away from the palm), palms facing each other, a fast approach, then the palms meet.
        // Hands resting together never clap. (DoubleClap.TryPalm's normal points out of the left palm but
        // out of the back of the right hand.)
        private readonly StrictClap strictClap = new StrictClap();
        public int StrictClaps => strictClap.Claps;
        public Vector3 ClapPoint => strictClap.Point;
        public float ClapPointAt => strictClap.PointAt;
        private bool StrictClapHeard()
        {
#if UNITY_EDITOR
            if (!UnityEngine.XR.XRSettings.isDeviceActive)
                return UnityEngine.InputSystem.Keyboard.current?.cKey.wasPressedThisFrame == true;
#endif
            float now = Time.unscaledTime;
            var left = HandSet.Side(Handedness.Left);
            var right = HandSet.Side(Handedness.Right);
            // (F51: no clap while a character stands on a palm: the other hand comes close to pet it or pick it up.)
            if (PalmPerch.Any || left == null || right == null || !left.isActiveAndEnabled || !right.isActiveAndEnabled || !left.IsConnected || !right.IsConnected ||
                !DoubleClap.TryPalm(left, out var a, out var na) || !DoubleClap.TryPalm(right, out var b, out var nb))
                return strictClap.Step(now, false, Vector3.zero, Vector3.zero, false, false, false, false);
            Vector3 across = (b - a).normalized;
            var head = handoff.meeting.Head;
            var offset = (a + b) * .5f - head.position;
            bool inFront = Vector3.Dot(offset, FlatForward(head)) > .1f && offset.sqrMagnitude < 1f;
            bool facing = Vector3.Dot(na, across) > .5f && Vector3.Dot(nb, across) > .5f;
            bool steady = left.IsHighConfidence && right.IsHighConfidence;
            bool ready = !left.GetIndexFingerIsPinching() && !right.GetIndexFingerIsPinching() &&
                         left.GetJointPose(HandJointId.HandIndexTip, out Pose li) && right.GetJointPose(HandJointId.HandIndexTip, out Pose ri) &&
                         Vector3.Distance(li.position, a) > .06f && Vector3.Distance(ri.position, b) > .06f;
            return strictClap.Step(now, true, a, b, facing, steady, ready, inFront);
        }

        // Where the hands met (or, from the keyboard in the Editor, where they would be), with the
        // clap's burst and sound there.
        private Vector3 ClapFeedback()
        {
            ClapFeedbacksPlayed++;
            var head = handoff.meeting.Head;
            Vector3 hands = Time.unscaledTime - ClapPointAt < .5f ? ClapPoint : head.position + FlatForward(head) * .32f - Vector3.up * .22f;
            ToyFx.ClapBurst(hands, head.position - hands);
            ToySounds.Play(ToySounds.Cue.Clap, hands, 1f);
            return hands;
        }

        // F29: during the session a clap brings the crew back around the person, wherever they are now.
        private bool callingOver;
        public bool CallingOver => callingOver;
        public void CallCrewOver()
        {
            if (!MakerActive || callingOver) return;
            var head = handoff.meeting.Head;
            var room = handoff.meeting.environment;
            Vector3 forward = FlatForward(head);
            int count = handoff.docks.Length;
            var found = GatherFormation.Find(room, head.position, forward, count);
            // The new seats are the meeting's seats now (the walk-back returns to them).
            if (found != null) formation = found;
            var layout = new ArrivalSeats(room, head.position, forward);
            var order = SeatOrder();
            var taken = new List<Vector3>();
            var walkers = new List<CapsulePetController>();
            for (int i = 0; i < count; i++)
            {
                var pet = handoff.docks[i].character.movement;
                if (pet.IsHeld) continue;
                var candidates = new List<Vector3>();
                if (found != null) { candidates.Add(found.Seats[order[i]]); candidates.AddRange(layout.RankedNear(found.Seats[order[i]], room)); }
                else candidates.AddRange(layout.Ranked(Mathf.Lerp(-40f, 40f, count > 1 ? i / (count - 1f) : .5f), room));
                pet.UnlockMeetingSeat();
                foreach (var seat in candidates)
                {
                    // F51: a seat with a made thing, a note or the mic on it: the free spot beside it.
                    Vector3 point = makerSession != null ? makerSession.ClearSeat(pet, seat, taken, false) : seat;
                    bool near = false;
                    foreach (var t in taken) if (Vector3.Distance(t, point) < GatherFormation.Settings.MinSpacing) { near = true; break; }
                    if (near) continue;
                    if (Vector3.Distance(pet.transform.position, point) < .06f) { taken.Add(point); break; }
                    if (!pet.GoTo(point, 3f)) continue;
                    taken.Add(point);
                    walkers.Add(pet);
                    break;
                }
            }
            SpeechBubbleDock.FaceFrom(head.position, found != null ? found.Center : head.position + forward);
            ClapFeedback();
            makerSession.CrewCalled();
            StartCoroutine(SettleCalledCrew(walkers));
            Debug.Log("[Windups Arrival] Clap: the crew comes over (" + walkers.Count + " walking).");
        }

        private IEnumerator SettleCalledCrew(List<CapsulePetController> walkers)
        {
            callingOver = true;
            float until = Time.time + 9f;
            yield return new WaitForSeconds(.3f);
            // On the way includes jumping up, hopping down and landing (2026-11-11).
            bool Walking() { foreach (var w in walkers) if (!w.IsHeld && w.State != CapsulePetController.MotionState.Idle && w.State != CapsulePetController.MotionState.WaitingForRoom) return true; return false; }
            while (Time.time < until && Walking()) yield return null;
            foreach (var dock in handoff.docks)
                for (int i = 0; i < 30 && !dock.character.movement.MeetingSeatLocked; i++)
                {
                    dock.character.movement.LockMeetingSeat();
                    if (!dock.character.movement.MeetingSeatLocked) yield return null;
                }
            callingOver = false;
        }

        // ------------------------------------------------------------ F78 airplane mode

        // No internet (WindupsNetwork, checked from Awake) in v4: the crew cannot talk to the AI or make anything, so
        // the person collects post-its instead (WindupsCollect). With internet everything is as before.
        public const float NetworkWait = 3.5f;
        public bool Offline => maker && makerSession != null && WindupsNetwork.Offline;
        // No room scan was loaded (airplane mode): the crew stands on the title sign.
        public bool Roomless => handoff != null && handoff.meeting != null && handoff.meeting.environment != null && handoff.meeting.environment.Roomless;
        private WindupsCollect collect;
        public WindupsCollect Collect => collect;

        // The crew is all here: the network check is waited for (at most NetworkWait). With no internet someone who was
        // here before collects at once; a new person gets the introduction first (its end, OfferTopicNote, collects).
        private IEnumerator MakerArrives()
        {
            float until = Time.time + NetworkWait;
            while (!WindupsNetwork.Known && Time.time < until) yield return null;
            if (Offline && (MakerSession.HasHistory || WindupsCollect.HasItems)) { StartCollect(false); yield break; }
            if (MakerSession.HasHistory) yield return ResumeMaker();
            else yield return IntroduceTeam();
            // Back online with things collected offline: they are shown too, to be used.
            if (!Offline && WindupsCollect.HasItems) ShowCollectedOnline();
        }

        // Collecting: no mic (nothing can be heard), no make. `introduced`: the introduction just said what this is.
        private void StartCollect(bool introduced)
        {
            if (collect != null) return;
            topicTaken = true;
            CurrentPhase = Phase.Meeting;
            guide.authoredBubble.gameObject.SetActive(false);
            guide.authoredBubble.GetComponent<SpeechBubblePresentation>().UseGuideCaption(false);
            if (microphoneCube != null) microphoneCube.SetActive(false);
            if (microphoneButton != null) microphoneButton.SetActive(false);
            if (pushToTalk != null) pushToTalk.enabled = false;
            collect = gameObject.AddComponent<WindupsCollect>();
            collect.Begin(this, makerSession, introduced);
            Debug.Log("[Windups Arrival] No internet: collecting (" + WindupsNetwork.Reason + (Roomless ? ", no room" : "") + ").");
        }

        private void ShowCollectedOnline()
        {
            if (collect != null) return;
            collect = gameObject.AddComponent<WindupsCollect>();
            collect.ShowOnline(this, makerSession);
        }

        // Back online: a collected post-it let go near the topic note (held out or in hand) gives it its words.
        public bool TopicNoteNear(Vector3 at) =>
            (CurrentPhase == Phase.OfferingTopic || CurrentPhase == Phase.WaitingForTopic || CurrentPhase == Phase.ReviewingTopic) &&
            TopicNote != null && TopicNote.gameObject.activeInHierarchy && Vector3.Distance(at, TopicNote.transform.position) < .14f;

        public bool UseCollectedWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (CurrentPhase == Phase.OfferingTopic && !topicTaken) TakeTopicNote();
            if (CurrentPhase != Phase.WaitingForTopic && CurrentPhase != Phase.ReviewingTopic) return false;
            HandleTranscriptUpdate(text);
            HandleTranscript(text);
            Debug.Log("[Windups Arrival] A collected post-it gave the topic note its words: " + text);
            return true;
        }

        // `carrier` holds out a blank post-it (the topic note), as Maker does with the first note.
        public void OfferNoteFrom(WindupCharacter carrier)
        {
            var note = TopicNote;
            if (note.transform.parent != null) note.transform.SetParent(null, true);
            note.gameObject.SetActive(true);
            if (topicPaper != null) topicPaper.UseStickyNoteShape();
            note.BeginTopicNote();
            UseNotePaper();
            var head = handoff.meeting.Head;
            Vector3 towardUser = Vector3.ProjectOnPlane(head.position - carrier.transform.position, Vector3.up);
            if (towardUser.sqrMagnitude < .001f) towardUser = -FlatForward(head);
            towardUser.Normalize();
            Vector3 at = carrier.transform.position + Vector3.up * .058f + towardUser * .075f;
            note.BeginCarriedTopicNote(carrier.transform, at, Quaternion.LookRotation(at - head.position, Vector3.up));
            note.SetText("Me", "");
            postIt?.OfferFromDeck();
            WindupBody.Of(carrier)?.Play("Decision_Present");
            ToySounds.Play(ToySounds.Cue.Paper, at, .6f, 1.2f);
            ToyFx.Puff(at, .01f, 3);
        }

        // The offered post-it was taken: it opens to be written on (by the keyboard).
        public void TakeCollectNote()
        {
            var note = TopicNote;
            if (note.transform.parent != null) note.transform.SetParent(null, true);
            note.BeginTopicNote();
            note.SetText("Me", "");
            postIt?.PullFromDeck();
            ToySounds.Play(ToySounds.Cue.Paper, note.transform.position, .9f, 1.1f);
            ToyFx.Puff(note.transform.position, .018f, 5);
            if (topicPaper != null) topicPaper.OpenFully();
        }

        // F29: someone who made something before is welcomed back straight away (no introductions).
        private IEnumerator ResumeMaker()
        {
            topicTaken = true;
            CurrentPhase = Phase.Meeting;
            guide.authoredBubble.gameObject.SetActive(false);
            guide.authoredBubble.GetComponent<SpeechBubblePresentation>().UseGuideCaption(false);
            PositionMicrophone();
            if (microphoneCube != null) microphoneCube.SetActive(true);
            if (microphoneButton != null) microphoneButton.SetActive(true);
            if (pushToTalk != null) pushToTalk.enabled = true;
            yield return null;
            makerSession.Welcome();
            Debug.Log("[Windups Arrival] Welcomed back (maker history).");
        }

#if UNITY_EDITOR
        public void PreviewClap(float now)
        {
            if (CurrentPhase == Phase.WaitingForClaps && clap.CountContact(now))
                BeginGathering();
        }

        // Editor: a click on the wall pad's note peels it, held in front of the view.
        public void PreviewPeelPadNote()
        {
            if (PadNote != PadNoteState.OnPad) return;
            PeelPadNote();
            var head = handoff.meeting.Head;
            TopicNote.transform.SetPositionAndRotation(head.position + head.forward * .45f - head.right * .13f - head.up * .12f, head.rotation);
        }

        public void PreviewReleaseNote() => OnTopicReleased();

        public void PreviewTakeTopicNote()
        {
            if (CurrentPhase != Phase.OfferingTopic) return;
            TakeTopicNote();
            var head = handoff.meeting.Head;
            var previewPosition = head.position + head.forward * .45f - head.right * .13f - head.up * .12f;
            TopicNote.transform.SetPositionAndRotation(previewPosition, head.rotation);
        }

        public void PreviewTranscript(string transcript)
        {
            HandleTranscript(transcript);
        }

        public void PreviewTopicTap(float now)
        {
            CountTopicTap(now);
        }

        // Editor shortcut for testing card interactions: run the arrival at 4x speed with a short
        // intro, write the topic and start the meeting. Scripted replies need no API key.
        private bool skipping;
        public bool PreviewSkipping => skipping;
        public void PreviewSkipToMeeting(string topic, bool scripted)
        {
            if (!skipping && CurrentPhase < Phase.Meeting) StartCoroutine(SkipToMeeting(topic, scripted));
        }

        private IEnumerator SkipToMeeting(string topic, bool scripted)
        {
            skipping = true;
            if (title != null) title.Skip();
            float hold = introHold;
            introHold = .4f;
            Time.timeScale = 4f;
            while (CurrentPhase == Phase.Entering) yield return null;
            if (CurrentPhase == Phase.WaitingForClaps) BeginGathering();
            while (CurrentPhase < Phase.OfferingTopic) yield return null;
            Time.timeScale = 1f;
            introHold = hold;
            if (CurrentPhase == Phase.OfferingTopic) PreviewTakeTopicNote();
            if (CurrentPhase == Phase.WaitingForTopic) HandleTranscript(topic);
            if (scripted) handoff.meeting.UseScriptedReplies();
            yield return null;
            if (CurrentPhase == Phase.ReviewingTopic) ConfirmTopic();
            if (scripted)
            {
                // The script first asks who it is for; answer so the six opinions arrive.
                float until = Time.time + 6f;
                while (!handoff.meeting.WaitingForAnswer && Time.time < until) yield return null;
                if (handoff.meeting.WaitingForAnswer) handoff.meeting.Submit("Kids who find it hard to start their homework.");
            }
            skipping = false;
        }

        private void OnDisable()
        {
            if (skipping) Time.timeScale = 1f;
        }
#endif

        private void WalkLocally()
        {
            var environment = handoff.meeting.environment;
            foreach (var dock in handoff.docks)
            {
                var pet = dock.character.movement;
                if (pet == guide.movement || pet.InteractionPaused || pet.State != CapsulePetController.MotionState.Idle ||
                    !nextWalk.TryGetValue(pet, out float next) || Time.time < next) continue;

                nextWalk[pet] = Time.time + Random.Range(5f, 9f);
                if (!homes.TryGetValue(pet, out var home) || Vector3.Distance(home, pet.transform.position) > .75f)
                    homes[pet] = home = pet.transform.position;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    Vector2 offset = Random.insideUnitCircle * .38f;
                    var target = home + new Vector3(offset.x, 0, offset.y);
                    if (environment.TrySameLevelNavPoint(target, out var point) &&
                        Vector3.Distance(home, point) <= .45f &&
                        Vector3.Distance(pet.transform.position, point) >= .15f &&
                        pet.GoTo(point, .65f)) break;
                }
            }
        }

        // F82 (Quest 2026-11-14, the user: "clap only the first time I enter the app; when I come back I don't need to"):
        // someone who has called the crew over once (or has made things here) is not asked to clap again: the crew comes
        // over at once. On the headset only (the Editor preview and its checks keep the clap cue).
        public const string ClapLearnedKey = "thinkmade.clapLearned";
        public static bool SkipClapInEditor;
        private bool ClapKnown => maker && makerSession != null && (!Application.isEditor || SkipClapInEditor) &&
                                  (PlayerPrefs.GetInt(ClapLearnedKey, 0) == 1 || MakerSession.HasHistory);

        // F96 (Quest 2026-11-15, the user: "when I come back, not the first start, remove the clap"): F82 skipped only
        // the clap cue but still played the clap's burst and clap sound in front of the person as the crew came over,
        // so coming back still "clapped". Now the crew just comes over: no guide bubble, no burst, no clap sound.
        public int ClapCuesShown { get; private set; }
        public int ClapFeedbacksPlayed { get; private set; }
        public bool CameWithoutClap { get; private set; }
        private void ComeOverWithoutClap()
        {
            CameWithoutClap = true;
            CurrentPhase = Phase.WaitingForClaps;
            Gather(false);
            Debug.Log("[Windups Arrival] Known person: the crew comes over without a clap.");
        }

        // (One method of this name: the older checks call it by name.)
        private void BeginGathering() => Gather(true);
        private void Gather(bool clapped)
        {
            if (maker && PlayerPrefs.GetInt(ClapLearnedKey, 0) != 1) { PlayerPrefs.SetInt(ClapLearnedKey, 1); PlayerPrefs.Save(); }
            var head = handoff.meeting.Head;
            gatherHead = head.position;
            gatherForward = FlatForward(head);
            var room = handoff.meeting.environment;
            var layout = new ArrivalSeats(room, gatherHead, gatherForward);
            int count = handoff.docks.Length;
            gatherCandidates = new List<Vector3>[count];
            // Decide the meeting spot first (nearest desk if possible) so the six gather
            // around the place where the topic ring will appear.
            GatherFormation.Settings = new GatherFormation.Layout
            {
                DeskRing = deskRingRadius, DeskGap = deskSeatGap, FloorRing = floorRingRadius, FloorGap = floorSeatGap, ArcSpan = arcSpan,
                MinSpacing = minCharacterSpacing, AroundUser = maker
            };
            formation = GatherFormation.Find(room, gatherHead, gatherForward, count);
            if (formation != null)
            {
                var order = SeatOrder();
                for (int i = 0; i < count; i++)
                {
                    var seat = formation.Seats[order[i]];
                    var candidates = new List<Vector3> { seat };
                    // If this seat can't be reached, stay with the group on the same surface first.
                    var near = layout.RankedNear(seat, room);
                    foreach (var point in near)
                        if (Mathf.Abs(point.y - seat.y) < .1f && Vector3.ProjectOnPlane(point - seat, Vector3.up).magnitude < .4f) candidates.Add(point);
                    foreach (var point in near) if (!candidates.Contains(point)) candidates.Add(point);
                    foreach (var point in layout.RankedNear(seat)) if (!candidates.Contains(point)) candidates.Add(point);
                    gatherCandidates[i] = candidates;
                    // Each speech card sits right above its own character's head.
                    handoff.docks[i].lift = cardLift;
                }
                Debug.Log("[Windups Arrival] Meeting spot on " + formation.SurfaceName + " furniture=" + formation.OnFurniture +
                          " ring=" + formation.RingRadius.ToString("F2"));
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    gatherCandidates[i] = layout.Ranked(Mathf.Lerp(-70f, 70f, i / (count - 1f)));
                    handoff.docks[i].lift = cardLift;
                }
                Debug.Log("[Windups Arrival] No formation fits; using ranked seats.");
            }
            seats.Clear();
            gatherStartedAt = Time.time;
            // Re-aim the shared (world-locked) card facing at the meeting spot once.
            SpeechBubbleDock.FaceFrom(gatherHead, formation != null ? formation.Center : gatherHead + gatherForward);
            foreach (var dock in handoff.docks) dock.character.movement.StopWalking();
            CurrentPhase = Phase.Gathering;
            // Clap feedback: a burst of impact lines where the hands met and a crisp clap (F96: only after a real clap).
            if (!clapped) return;
            ClapFeedback();
            Debug.Log("[Windups Arrival] Clap recognized; gathering.");
        }

        private void UpdateGathering()
        {
            if (Time.time < nextGatherAttempt) return;
            nextGatherAttempt = Time.time + .5f;
            int arrived = 0;
            for (int i = 0; i < handoff.docks.Length; i++)
            {
                var pet = handoff.docks[i].character.movement;
                if (pet.IsHeld || pet.InteractionPaused || pet.State != CapsulePetController.MotionState.Idle) continue;
                if (seats.TryGetValue(pet, out var seat) && Vector3.Distance(pet.transform.position, seat) <= .09f)
                {
                    pet.LockMeetingSeat();
                    arrived++;
                    continue;
                }
                // A character that still can't reach any seat after a while stays where it is
                // (never teleported) so the introductions can begin.
                if (Time.time - gatherStartedAt > GatherPatience && !pet.IsHeld)
                {
                    seats[pet] = pet.transform.position;
                    pet.LockMeetingSeat();
                    arrived++;
                    Debug.Log("[Windups Arrival] " + handoff.docks[i].character.characterName + " stays in place; no reachable seat.");
                    continue;
                }
                seats.Remove(pet);
                TryAssignSeat(pet, gatherCandidates[i], float.PositiveInfinity);
            }
            if (!guideDismissed)
            {
                guide.authoredBubble.gameObject.SetActive(false);
                guideDismissed = true;
            }
            if (arrived != handoff.docks.Length) return;
            Debug.Log("[Windups Arrival] All " + handoff.docks.Length + " characters gathered.");
            CurrentPhase = Phase.Introducing;
            if (maker && makerSession != null) StartCoroutine(MakerArrives()); // F78: waits for the network check
            else StartCoroutine(IntroduceTeam());
        }

        // Seat index for each dock: Information in the middle, the others filling outward.
        private int[] SeatOrder()
        {
            int count = handoff.docks.Length;
            var order = new int[count];
            int middle = (count - 1) / 2, next = 0;
            var free = new List<int>();
            for (int k = 0; k < count; k++) if (k != middle) free.Add(k);
            for (int i = 0; i < count; i++)
                order[i] = handoff.docks[i].character == guide ? middle : free[next++];
            return order;
        }

        // Each teammate's profile: what kind of thinker it is and what it can do for the user.
        private static readonly Dictionary<string, (string role, string[] jobs)> Profiles = new Dictionary<string, (string, string[])>
        {
            { "Information", ("The fact finder", new[] { "Checks what we already know", "Finds data and examples", "Separates facts from guesses" }) },
            { "Emotions", ("The heart of the team", new[] { "Asks how an idea feels", "Shares honest gut reactions", "Keeps people in mind" }) },
            { "Benefit", ("The optimist", new[] { "Finds the upside", "Spots quick wins", "Builds on what works" }) },
            { "Caution", ("The careful one", new[] { "Spots risks early", "Asks what could go wrong", "Checks costs and limits" }) },
            { "Creativity", ("The idea maker", new[] { "Suggests new directions", "Mixes unexpected ideas", "Asks \"what if...?\"" }) },
            { "Decisions", ("The guide", new[] { "Keeps the meeting on track", "Sums up the options", "Helps pick the next step" }) }
        };

        public IReadOnlyCollection<CharacterIntroCard> IntroCards => introCards.Values;

        // Where a character's name tag floats: just above the head, in front like its cards.
        private static Vector3 TagPoint(SpeechBubbleDock dock) =>
            dock.anchor.position + dock.anchor.rotation * new Vector3(0f, .016f, -SpeechBubbleDock.ForwardOffset);

        // Onboarding (design B, "dealt trading cards"): Information holds a small face-down
        // deck and deals one profile card to each teammate (fly, flip, read). Each card then
        // shrinks into a name tag above its owner's head; tapping a tag shows the profile again.
        [System.NonSerialized] public float introHold = 6f;
        private IEnumerator IntroduceTeam()
        {
            IntroducedCount = 0;
            var look = guide.authoredBubble.GetComponent<SpeechBubblePresentation>();
            var material = guide.authoredBubble.GetComponent<MeshRenderer>().sharedMaterial;
            var font = look != null ? look.toyFont : null;
            var head = handoff.meeting.Head;
            ShowGuide(maker && makerSession != null ? MakerSession.IntroOpening : "Let me introduce the team!");
            // Hello, each in its own way and its own moment (the guide first).
            foreach (var dock in handoff.docks)
            {
                var greeter = WindupBody.Of(dock.character);
                if (greeter == null) continue;
                if (dock.character == guide) greeter.Play("Wave");
                else greeter.PlaySoon(WindupBody.Greeting(dock.character.characterName), .9f);
            }
            bool makerPages = maker && makerSession != null; // 2026-11-13: the person turns the lines with < > (MakerIntroPages)
            if (!makerPages) yield return new WaitForSeconds(2.2f);
            else yield return null;

            var ordered = new List<SpeechBubbleDock>();
            foreach (var dock in handoff.docks) if (dock.character == guide) ordered.Add(dock);
            foreach (string role in new[] { "Emotions", "Benefit", "Caution", "Creativity", "Decisions" })
                foreach (var dock in handoff.docks) if (dock.character.characterName == role && !ordered.Contains(dock)) ordered.Add(dock);
            foreach (var dock in handoff.docks) if (!ordered.Contains(dock)) ordered.Add(dock);

            // Each profile card pops straight up out of its owner's head (nothing is dealt).
            for (int i = 0; i < ordered.Count; i++)
            {
                string role = ordered[i].character.characterName;
                if (introCards.ContainsKey(role)) continue;
                Profiles.TryGetValue(role, out var profile);
                string shown = role, line = profile.role ?? "";
                string[] jobs = profile.jobs;
                Color color = SpeechBubblePresentation.ResolveRoleColor(role);
                if (maker && MakerRoles.TryForCharacter(role, out var crew))
                {
                    shown = MakerRoles.Name(crew);
                    line = MakerRoles.Profile(crew);
                    jobs = MakerRoles.Jobs(crew);
                    color = MakerRoles.Color(crew);
                }
                var card = CharacterIntroCard.Create(transform, shown, line, jobs, color, material, font, IntroPitch(role));
                introCards[role] = card;
                card.Owner = ordered[i].character.movement.transform;
            }

            if (makerPages)
            {
                yield return MakerIntroPages(ordered);
                OfferTopicNote();
                yield break;
            }

            foreach (var dock in ordered)
            {
                var actor = dock.character;
                string role = actor.characterName;
                var card = introCards[role];
                Profiles.TryGetValue(role, out var who);
                ShowGuide(actor == guide ? "This one's me, " + (who.role ?? "the fact finder").ToLowerInvariant() + "!"
                                         : "Meet " + role + ", " + (who.role ?? role).ToLowerInvariant() + ".");
                yield return null;
                Vector3 tagAt = TagPoint(dock);
                // Everyone's card rises to the same height just above the head. Information is
                // still talking through its own caption there, so its card stands beside it,
                // toward the middle of the group, instead of high above it.
                Vector3 reveal = tagAt + Vector3.up * .08f;
                if (actor == guide)
                {
                    Vector3 right = SpeechBubbleDock.FacingAt(tagAt) * Vector3.right;
                    float side = formation == null || Vector3.Dot(FormationCenter - tagAt, right) >= 0f ? 1f : -1f;
                    var caption = guide.authoredBubble.GetComponent<TangibleBubble>();
                    float half = caption != null ? caption.Width * .5f : .12f;
                    reveal += right * side * (half + .11f + .02f);
                }
                yield return card.PopUp(reveal, tagAt, introHold, () =>
                {
                    Vector3 feet = actor.movement.transform.position + Vector3.up * .004f; // the pet root is at its feet
                    ToyFx.Ring(feet, Quaternion.Euler(90f, 0f, 0f), .075f);
                    var body = WindupBody.Of(actor);
                    body?.Play(WindupBody.Introduction(role));
                    actor.SetCue("encourage");
                    IntroducedCount++;
                });
                actor.SetCue("idle");
            }
            if (maker) yield return new WaitForSeconds(.8f);
            else
            {
                ShowGuide("Point at any of us to see our name. Tap it to see what we do.");
                yield return new WaitForSeconds(2.4f);
            }
            OfferTopicNote();
        }

        // 2026-11-13 (the user after a Quest try: "the introduction is too fast - arrows on Maker's bubble so I can
        // turn it myself"; they chose: only when I press >). Maker's lines are pages: the opening, each teammate's
        // lines (its profile card up while they are said, each line with the tool it is about held up), the closing line (no mic: it is not out yet).
        // > (MakerSession.IntroPager.cs) shows the next page, < the one before; nothing goes on by itself. Checks and
        // the Editor's skip go on with the old timing (MakerSession.IntroAutoAdvance, PreviewSkipping).
        public int IntroPage { get; private set; } = -1;
        public int IntroPageCount { get; private set; }
        private IEnumerator MakerIntroPages(List<SpeechBubbleDock> ordered)
        {
            var pages = new List<(SpeechBubbleDock dock, MakerSession.IntroStep step)>
            { (null, new MakerSession.IntroStep(MakerSession.IntroOpening, null, 2.2f)) };
            foreach (var dock in ordered)
                if (dock.character != null && MakerRoles.TryForCharacter(dock.character.characterName, out var crew))
                    foreach (var step in MakerSession.IntroSteps(crew)) pages.Add((dock, step));
            pages.Add((null, new MakerSession.IntroStep(MakerSession.IntroClosing, null, 2.8f)));
            IntroPageCount = pages.Count;
            SpeechBubbleDock showing = null;            // whose profile card is up
            var introduced = new HashSet<SpeechBubbleDock>();
            var pops = new Dictionary<CharacterIntroCard, Coroutine>();
#if UNITY_EDITOR
            bool Auto() => skipping || MakerSession.IntroAutoAdvance;
#else
            bool Auto() => MakerSession.IntroAutoAdvance; // (`skipping` is the Editor's skip only: the player build has no such field)
#endif
            int i = 0;
            while (i < pages.Count)
            {
                IntroPage = i;
                var (dock, step) = pages[i];
                ShowGuide(step.line, dock != null ? dock.character : null); // F79: each teammate's own mumble
                if (dock != showing)
                {
                    if (showing != null) showing.character.SetCue("idle");
                    showing = dock;
                    if (dock != null) PopIntroCard(dock, introduced, pops, () => showing == dock);
                }
                makerSession.ShowIntroTool(dock != null ? dock.character.characterName : guide.characterName, step.tool, step.seconds - .15f, !Auto());
                // F79 (Quest: "a strange ding after Spark's introduction"): the closing page no longer rings and dings at
                // the mic, which is not out yet at this point.
                if (Auto())
                {
                    makerSession.HideIntroPager();
                    for (float until = Time.time + step.seconds; Time.time < until; ) { KeepCardOffCaption(showing); yield return null; }
                    i++;
                    continue;
                }
                makerSession.ShowIntroPager(i > 0, 1.2f + step.line.Length * .05f);
                int turn = 0;
                while ((turn = makerSession.TakeIntroPage()) == 0 && !Auto()) { KeepCardOffCaption(showing); yield return null; }
                i = Mathf.Max(0, i + (turn < 0 ? -1 : 1));
            }
            IntroPage = -1;
            makerSession.HideIntroPager();
            if (showing != null) showing.character.SetCue("idle");
            showing = null;                                  // the last card goes down into its tag
            makerSession.EndIntroTools();
            MakerSession.MarkToolsTaught();
        }

        // A teammate's profile card pops up out of its head and stays while `keep` holds (the first time with its ring,
        // introduction clip and cue; again after <, just the card).
        private void PopIntroCard(SpeechBubbleDock dock, HashSet<SpeechBubbleDock> introduced, Dictionary<CharacterIntroCard, Coroutine> pops, System.Func<bool> keep)
        {
            var actor = dock.character;
            string role = actor.characterName;
            if (!introCards.TryGetValue(role, out var card) || card == null) return;
            Vector3 tagAt = TagPoint(dock);
            Vector3 reveal = actor == guide ? GuideCardSpot(dock, card) : tagAt + Vector3.up * .08f; // F80
            if (pops.TryGetValue(card, out var running) && running != null) StopCoroutine(running);
            bool first = introduced.Add(dock);
            pops[card] = StartCoroutine(card.PopUpWhile(reveal, tagAt, keep, () =>
            {
                actor.SetCue("encourage");
                if (!first) return;
                Vector3 feet = actor.movement.transform.position + Vector3.up * .004f;
                ToyFx.Ring(feet, Quaternion.Euler(90f, 0f, 0f), .075f);
                WindupBody.Of(actor)?.Play(WindupBody.Introduction(role));
                IntroducedCount++;
            }));
        }

        // (2026-11-13, the user: "UI covers the speech bubbles") A teammate's profile card standing over Maker's speech
        // card, as the person sees it, slides sideways off it (away from Maker), every frame while it is up.
        public int CardsMovedOffCaption { get; private set; }
        // F80 (Quest 2026-11-14: "Maker's name tag overlapped its speech bubble while it introduced itself"): Maker's own
        // card was put beside its speech card once, at the width of the first line, 2 cm into the < > arrows (F68b made
        // them 1.6x bigger) and never moved when a longer line widened the card. Now every frame it stands clear of the
        // card AND its arrows, on the side toward the group, at the card's current width.
        public const float GuideCardGap = .02f;
        public const float BubbleOverTag = .01f;

        // F81: the highest name tag over this character (float.MinValue: none).
        public float NameTagTop(SpeechBubbleDock dock)
        {
            float top = float.MinValue;
            if (dock?.character == null) return top;
            if (introCards.TryGetValue(dock.character.characterName, out var card) && card != null && card.ShowsTag) top = card.TagTop;
            if (makerSession != null && MakerRoles.TryForCharacter(dock.character.characterName, out var crew))
            {
                var tag = makerSession.CrewNameTag(crew);
                if (tag != null && tag.gameObject.activeInHierarchy) top = Mathf.Max(top, tag.position.y + MakerSession.CrewTagHeight * .5f);
            }
            return top;
        }
        private void KeepGuideCardBeside(SpeechBubbleDock dock)
        {
            if (dock == null || dock.character != guide || !introCards.TryGetValue(dock.character.characterName, out var card) || card == null ||
                card.Current != CharacterIntroCard.State.Revealed) return;
            Vector3 want = GuideCardSpot(dock, card);
            if ((card.RevealedAt - want).sqrMagnitude > 1e-5f) GuideCardMoves++;
            card.MoveRevealed(want);
        }

        // Where Maker's own card stands: beside its speech card, clear of the card's current width and the < > arrows,
        // on the side toward the group, at the height it always rose to.
        private Vector3 GuideCardSpot(SpeechBubbleDock dock, CharacterIntroCard card)
        {
            Vector3 tagAt = TagPoint(dock);
            Vector3 reveal = tagAt + Vector3.up * .08f;
            if (guide == null || guide.authoredBubble == null) return reveal;
            var caption = guide.authoredBubble.transform;
            float halfX = .12f;
            if (caption.gameObject.activeInHierarchy && caption.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
                halfX = filter.sharedMesh.bounds.extents.x * caption.lossyScale.x;
            Vector3 right = SpeechBubbleDock.FacingAt(caption.position) * Vector3.right;
            float side = formation == null || Vector3.Dot(FormationCenter - tagAt, right) >= 0f ? 1f : -1f;
            float arrows = maker && makerSession != null ? MakerSession.PagerRadius * MakerSession.PagerScale * 2f + .004f : 0f;
            float across = halfX + arrows + GuideCardGap + card.CardSize.x * .5f;
            Vector3 centre = caption.gameObject.activeInHierarchy ? caption.position : tagAt;
            Vector3 at = centre + right * side * across;
            // (sideways measured from the speech card's centre; the height stays the card's own)
            return new Vector3(at.x, reveal.y, at.z);
        }
        public int GuideCardMoves { get; private set; }

        private void KeepCardOffCaption(SpeechBubbleDock dock)
        {
            if (dock != null && dock.character == guide) { KeepGuideCardBeside(dock); return; }
            if (dock == null || dock.character == guide || !introCards.TryGetValue(dock.character.characterName, out var card) || card == null ||
                card.Current != CharacterIntroCard.State.Revealed || guide == null || guide.authoredBubble == null || !guide.authoredBubble.gameObject.activeInHierarchy) return;
            var head = handoff.meeting.Head;
            if (head == null) return;
            var caption = guide.authoredBubble.transform;
            Vector2 half = new Vector2(.1f, .04f);
            if (caption.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
                half = new Vector2(filter.sharedMesh.bounds.extents.x * caption.lossyScale.x, filter.sharedMesh.bounds.extents.y * caption.lossyScale.y);
            // In the person's view plane at the caption: its sideways axis, and where the card's centre falls.
            Vector3 want = TagPoint(dock) + Vector3.up * .08f;
            Vector3 right = SpeechBubbleDock.FacingAt(caption.position) * Vector3.right;
            float scale = Vector3.Distance(head.position, caption.position) / Mathf.Max(.05f, Vector3.Distance(head.position, want));
            Vector3 seen = head.position + (want - head.position) * scale; // the card's centre carried to the caption's distance
            float dx = Vector3.Dot(seen - caption.position, right), dy = seen.y - caption.position.y;
            Vector2 size = card.CardSize * .5f * scale;
            float needX = half.x + size.x + .045f, needY = half.y + size.y + .025f; // room for the < > buttons and the card's bob
            if (Mathf.Abs(dx) < needX && Mathf.Abs(dy) < needY)
            {
                float away = Mathf.Abs(dx) > .005f ? Mathf.Sign(dx) : Vector3.Dot(dock.character.transform.position - guide.transform.position, right) >= 0f ? 1f : -1f;
                want += right * (away * (needX - Mathf.Abs(dx)) / scale);
                if ((card.RevealedAt - want).sqrMagnitude > 1e-6f) CardsMovedOffCaption++;
            }
            card.MoveRevealed(want);
        }

        // Name tags are hidden until hovered: a fingertip near the character, a hand pointing
        // at it, or (Editor preview) the mouse over it. Then poke the tag, pinch while
        // pointing from a distance, or click to open the profile. One profile at a time.
        private readonly Dictionary<Hand, bool> wasPinching = new Dictionary<Hand, bool>();
        private readonly HashSet<CharacterIntroCard> poked = new HashSet<CharacterIntroCard>();
        private readonly HashSet<CharacterIntroCard> hovered = new HashSet<CharacterIntroCard>();
        private float lastTagToggle;
        private bool UpdateTagTaps()
        {
            if (introCards.Count == 0) return false;
            hovered.Clear();
            bool toggled = false;
#if UNITY_EDITOR
            if (!UnityEngine.XR.XRSettings.isDeviceActive && Camera.main != null && UnityEngine.InputSystem.Mouse.current is { } mouse)
            {
                var ray = Camera.main.ScreenPointToRay(mouse.position.ReadValue());
                var target = Nearest(ray);
                if (target != null) hovered.Add(target);
                if (target != null && mouse.leftButton.wasPressedThisFrame) { ToggleCard(target); toggled = true; }
            }
#endif
            var touching = new HashSet<CharacterIntroCard>();
            foreach (var hand in HandSet.Both())
                {
                    if (hand == null || !hand.isActiveAndEnabled || !hand.IsConnected) continue;
                    if (hand.GetJointPose(HandJointId.HandIndexTip, out Pose tip))
                        foreach (var card in introCards.Values)
                        {
                            if (card.Near(tip.position)) hovered.Add(card);
                            if (card.TagVisible && card.Touches(tip.position)) touching.Add(card);
                        }
                    if (!hand.GetPointerPose(out Pose pointer)) continue;
                    // Pointing shows the tag; a far pinch-tap on it (or on the character) opens it
                    // through RemoteGrab, so a far drag of the character does not also toggle it.
                    var aimed = Nearest(new Ray(pointer.position, pointer.forward));
                    if (aimed != null) hovered.Add(aimed);
                }
            foreach (var card in touching)
                if (!poked.Contains(card) && Time.time - lastTagToggle > .5f) { ToggleCard(card); toggled = true; }
            poked.Clear();
            poked.UnionWith(touching);
            foreach (var card in introCards.Values) card.SetHovered(hovered.Contains(card));
            return toggled;
        }

        private CharacterIntroCard Nearest(Ray ray)
        {
            CharacterIntroCard best = null;
            float bestDistance = 4f;
            foreach (var card in introCards.Values)
                if (card.IsTagReady && card.Raycast(ray, out float distance) && distance < bestDistance) { best = card; bestDistance = distance; }
            return best;
        }

        // Far ray hit on a visible name tag or profile (for RemoteGrab).
        public CharacterIntroCard TagHit(Ray ray, out float distance)
        {
            distance = float.PositiveInfinity;
            CharacterIntroCard best = null;
            foreach (var card in introCards.Values)
                if (card.IsTagReady && (card.TagVisible || card.Current == CharacterIntroCard.State.Expanded) &&
                    card.Raycast(ray, out float d) && d < distance) { best = card; distance = d; }
            return best;
        }

        // A far tap on a character opens or closes its name card.
        // Removes the intro name tags.
        public void RetireIntroTags()
        {
            foreach (var card in introCards.Values) if (card != null) Destroy(card.gameObject);
            introCards.Clear();
        }

        public void TogglePetTag(RoomCapsule.CapsulePetController pet)
        {
            foreach (var card in introCards.Values)
                if (card.Owner == pet.transform && card.IsTagReady) { ToggleCard(card); return; }
        }

        public void ToggleCard(CharacterIntroCard card)
        {
            lastTagToggle = Time.time;
            foreach (var other in introCards.Values) if (other != card) other.Collapse();
            card.Toggle();
        }

        private static float IntroPitch(string role)
        {
            switch (role)
            {
                case "Emotions": return 1.12f;
                case "Benefit": return 1.06f;
                case "Caution": return .88f;
                case "Creativity": return 1.18f;
                case "Decisions": return .94f;
                default: return 1f;
            }
        }

        private void OfferTopicNote()
        {
            if (Offline) { StartCollect(true); return; } // F78: every way the introduction ends
            var note = TopicNote;
            note.gameObject.SetActive(true);
            topicPaper.UseStickyNoteShape();
            note.BeginTopicNote();
            note.SetText("Information", "");
            UseNotePaper();
            var head = handoff.meeting.Head;
            Vector3 towardUser = Vector3.ProjectOnPlane(head.position - guide.transform.position, Vector3.up);
            if (towardUser.sqrMagnitude < .001f) towardUser = -FlatForward(head);
            towardUser.Normalize();
            // Held out at hand height (the pad under it), so the guide's face stays visible.
            Vector3 notePosition = guide.transform.position + Vector3.up * .058f + towardUser * .075f;
            Quaternion noteRotation = Quaternion.LookRotation(notePosition - head.position, Vector3.up);
            TopicNote.BeginCarriedTopicNote(guide.transform, notePosition, noteRotation);
            TopicNote.SetText("Information", "");
            postIt?.OfferFromDeck();
            WindupBody.Of(guide)?.Play("Decision_Present");
            ShowGuide("Take this.");
            CurrentPhase = Phase.OfferingTopic;
            Debug.Log("[Windups Arrival] Information is carrying the small topic note.");
        }

        private void UseNotePaper()
        {
            if (TopicNote.TryGetComponent<MeshRenderer>(out var renderer))
            {
                // The guide can still be hidden by the room spawn sequence when cloned.
                renderer.enabled = true;
                if (topicNoteMaterial != null)
                {
                    // Soft pastel paper rather than a saturated toy block.
                    if (noteMaterial == null)
                    {
                        noteMaterial = new Material(topicNoteMaterial) { name = "Topic Note Paper" };
                        noteMaterial.SetColor("_BaseColor", new Color(1f, .93f, .62f));
                        noteMaterial.SetColor("_ShadeColor", new Color(.95f, .92f, .84f));
                        noteMaterial.SetFloat("_RimStrength", .05f);
                    }
                    renderer.sharedMaterial = noteMaterial;
                }
            }
        }

        // F29 idea wall: a blank note waits as the top sheet of the pad (the pad is drawn behind it).
        public void PutNoteOnPad(Vector3 position, Quaternion rotation, float scale)
        {
            var note = TopicNote;
            if (note.transform.parent != null) note.transform.SetParent(null, true);
            note.gameObject.SetActive(true);
            if (topicPaper != null)
            {
                topicPaper.UseStickyNoteShape();
                // As big as the notes beside it (the wall board grows with distance).
                topicPaper.compactWidth *= scale;
                topicPaper.compactHeight *= scale;
            }
            note.BeginTopicNote();
            note.SetText("Me", "");
            UseNotePaper();
            note.transform.SetPositionAndRotation(position, rotation);
            postIt?.OfferFromDeck();
            PadNote = PadNoteState.OnPad;
        }

        // Peeled off the wall pad: it opens to be written on; the next thing said is the new idea.
        private void PeelPadNote()
        {
            PadNote = PadNoteState.Peeled;
            if (TopicNote.transform.parent != null) TopicNote.transform.SetParent(null, true);
            TopicNote.BeginTopicNote();
            TopicNote.SetText("Me", "");
            postIt?.PullFromDeck(true);
            ToySounds.Play(ToySounds.Cue.Paper, TopicNote.transform.position, .9f, 1.1f);
            ToyFx.Puff(TopicNote.transform.position, .014f, 4);
            if (topicPaper != null) topicPaper.OpenFully();
            if (pushToTalk != null) pushToTalk.enabled = true;
            makerSession.PadNotePeeled();
            Debug.Log("[Windups Arrival] A note was peeled off the wall pad.");
        }

        // Written and let go (or left alone a moment): it goes up on the wall with the others.
        private IEnumerator StickWhenLeft()
        {
            yield return new WaitForSeconds(1.8f);
            if (PadNote == PadNoteState.Written && !TopicNote.IsHeld) StickWrittenNote();
        }

        private void StickWrittenNote()
        {
            PadNote = PadNoteState.None;
            makerSession.StickWrittenNote(TopicNote.transform);
        }

        private void TakeTopicNote()
        {
            topicTaken = true;
            if (TopicNote.transform.IsChildOf(guide.transform))
                TopicNote.transform.SetParent(null, true);
            TopicNote.BeginTopicNote();
            TopicNote.SetText("Me", "");
            postIt?.PullFromDeck();
            ToySounds.Play(ToySounds.Cue.Paper, TopicNote.transform.position, .9f, 1.1f);
            ToyFx.Puff(TopicNote.transform.position, .018f, 5);
            CurrentPhase = Phase.WaitingForTopic;
            // F45: tell it with the mic, or press Find ideas (over Maker's card) and the crew looks around.
            ShowGuide(maker ? OpeningQuestion : "What would you like us\nto create together today?");
            if (maker && makerSession != null) makerSession.ShowIdeasButton();
            if (topicPaper != null) topicPaper.OpenFully();
            PositionMicrophone();
            if (microphoneCube != null) microphoneCube.SetActive(true);
            if (microphoneButton != null) microphoneButton.SetActive(true);
            handoff.meeting.status.text = "Hold the microphone and tell us what you would like to create together.";
            Debug.Log("[Windups Arrival] Topic note taken; waiting for push-to-talk.");
        }

        private void PositionMicrophone()
        {
            if (microphoneCube == null || microphoneButton == null || handoff.meeting.Head == null) return;
            MicrophonePose(handoff.meeting.Head, out var position, out var rotation);
            microphoneCube.transform.SetPositionAndRotation(position, rotation);
            microphoneButton.transform.SetPositionAndRotation(position, rotation);
            micPlacedFrom = handoff.meeting.Head.position;
            micMoving = false;
        }

        private void HandleTranscriptUpdate(string transcript)
        {
            // F72: the note on the wall waits for new words (its Rewrite button): they are written on it as they come.
            if (CurrentPhase == Phase.Meeting && MakerActive && makerSession.NoteRewriting && PadNote != PadNoteState.Peeled)
            {
                makerSession.ShowRewriteLive(transcript);
                return;
            }
            if (CurrentPhase == Phase.Meeting && PadNote == PadNoteState.Peeled)
            {
                if (!string.IsNullOrWhiteSpace(transcript) && !makerSession.IsRequest(transcript)) TopicNote.SetText("Me", transcript, transcript);
            }
            else if (CurrentPhase == Phase.WaitingForTopic)
                TopicNote.SetText("Me", transcript, transcript);
            else if (CurrentPhase == Phase.ReviewingTopic && !string.IsNullOrWhiteSpace(transcript))
            {
                // Speaking again continues the note, shown live after what is already there.
                string live = Join(topicDraft, transcript);
                TopicNote.SetText("Me", live, live);
            }
            else if (CurrentPhase == Phase.ReviewingTopic && !string.IsNullOrWhiteSpace(topicDraft))
                TopicNote.SetText("Me", topicDraft, topicDraft);
        }

        private static string Join(string first, string second)
        {
            first = first?.Trim() ?? "";
            second = second?.Trim() ?? "";
            if (first.Length == 0) return second;
            if (second.Length == 0) return first;
            return first + " " + second;
        }

        private bool HandleTranscript(string transcript)
        {
            // Maker, a note peeled off the wall pad: what is said is the new idea.
            if (CurrentPhase == Phase.Meeting && MakerActive && PadNote == PadNoteState.Peeled)
            {
                string said = transcript?.Trim() ?? "";
                if (said.Length == 0) { TopicNote.SetText("Me", ""); return true; }
                // Talking to the crew with a blank note in hand ("Maker, can you give me the sketchbook?"):
                // a request, not the note's words.
                if (makerSession.IsRequest(said)) { TopicNote.SetText("Me", ""); return makerSession.OnVoice(said); }
                PadNote = PadNoteState.Written;
                TopicNote.SetText("Me", said);
                makerSession.NewIdea(said);
                if (!TopicNote.IsHeld) StartCoroutine(StickWhenLeft());
                return true;
            }
            // Maker: what the space needs, sizes, yes / no, changes, "print it".
            if (CurrentPhase == Phase.Meeting && MakerActive) return makerSession.OnVoice(transcript);
            if (CurrentPhase == Phase.ReviewingTopic && IsTopicConfirmation(transcript))
            {
                ConfirmTopic();
                return true;
            }
            if (CurrentPhase != Phase.WaitingForTopic && CurrentPhase != Phase.ReviewingTopic) return false;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                if (CurrentPhase == Phase.ReviewingTopic) TopicNote.SetText("Me", topicDraft, topicDraft);
                return true;
            }
            // F45: at the first note, asking for ideas is the Find ideas pill; "I don't know" only points at it.
            if (maker && makerSession != null && CurrentPhase == Phase.WaitingForTopic)
            {
                string asked = transcript.Trim().ToLowerInvariant();
                if (MakerSession.AsksForIdeas(asked)) { TopicNote.SetText("Me", ""); makerSession.FindIdeas("said"); return true; }
                if (MakerSession.StuckWords(asked))
                {
                    TopicNote.SetText("Me", "");
                    ShowGuide("No worries! Tap Find ideas\nand we'll look around your room.");
                    makerSession.ShowIdeasButton();
                    makerSession.PulseIdeasButton();
                    return true;
                }
                makerSession.HideIdeasButton();
            }
            // First take writes the note; later takes during review continue it.
            topicDraft = CurrentPhase == Phase.ReviewingTopic ? Join(topicDraft, transcript) : transcript.Trim();
            // Maker: "a coaster for this mug" -> Spark finds that mug (where the finger points, or by looking) and tags it.
            if (maker && makerSession != null) makerSession.Spot(transcript);
            TopicNote.SetText("Me", topicDraft);
            // F72: no ring with Maker: the note's own buttons (Rewrite, the green check) or "I'm done".
            if (NoteOnWall) ShowGuide("Wrong words? Tap Rewrite on the note.\nSay ‘I’m done’ or tap the green check.");
            else
            {
                ShowGuide("Wrong words? Tap the note to say it again.\nHold the mic to add more, or put it in the ring.");
                ShowTopicRing();
            }
            microphone.ShowMeetingHint(TopicRing != null && TopicRing.enabled ? "Put the note in the ring\nor say ‘I’m done’" : "Say ‘I’m done’ to start");
            if (topicTapSurface != null) topicTapSurface.raycastTarget = !NoteOnWall;
            if (pushToTalk != null) pushToTalk.enabled = true;
            CurrentPhase = Phase.ReviewingTopic;
            firstTopicTap = float.NegativeInfinity;
            return true;
        }

        private static bool IsTopicConfirmation(string transcript)
        {
            string phrase = transcript?.Trim().TrimEnd('.', '!', '?', '。').Replace('’', (char)39).ToLowerInvariant() ?? "";
            if (phrase == "i'm done" || phrase == "im done" || phrase == "i am done" ||
                phrase == "all set" || phrase == "다 됐어" || phrase == "다됐어") return true;
            // Short confirmations that speech-to-text often decorates ("Okay, I'm done now.").
            int words = phrase.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries).Length;
            return words <= 5 && (phrase.Contains("i'm done") || phrase.Contains("i am done") || phrase.Contains("im done") ||
                                  phrase.Contains("that's it") || phrase.Contains("다 됐") || phrase.Contains("끝났어"));
        }

        // The ring in the middle of the crew (shown if it is not yet): where this session's note stands.
        public bool EnsureRing(out Vector3 centre, out float radius)
        {
            // F72: with Maker the note is on the wall: there is no ring.
            if (NoteOnWall) { centre = Vector3.zero; radius = ringRadius; return false; }
            ShowTopicRing();
            centre = TopicRing != null ? TopicRing.transform.position : Vector3.zero;
            radius = ringRadius;
            return TopicRing != null;
        }

        private void ShowTopicRing()
        {
            if (TopicRing != null)
            {
                if (!TopicRing.enabled) { TopicRing.enabled = true; ringShownAt = Time.time; }
                return;
            }
            var head = handoff.meeting.Head;
            var room = handoff.meeting.environment;
            Vector3 center;
            if (formation != null)
            {
                // The spot the six already gathered around (usually the desk).
                ringRadius = formation.RingRadius;
                center = formation.Center + Vector3.up * .006f;
            }
            else if (!TryRingNearSeats(head, room, out center) && !TryFloorRingSpot(head, room, out center)) return;

            var ring = new GameObject("Topic confirmation ring", typeof(MeshFilter), typeof(MeshRenderer));
            ring.transform.SetParent(transform, true);
            ring.transform.SetPositionAndRotation(center, Quaternion.Euler(90f, 0f, 0f));
            var renderer = ring.GetComponent<MeshRenderer>();
            TopicRing = renderer;
            TopicRing.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            TopicRing.receiveShadows = false;
            var toy = guide.authoredBubble.GetComponent<MeshRenderer>().sharedMaterial;
            ringMaterial = new Material(toy != null ? toy : TopicNote.GetComponent<MeshRenderer>().sharedMaterial);
            TopicRing.sharedMaterial = ringMaterial;
            // The look (TopicRingLook): the slim ring is about 8 mm thick whatever its size; every look is
            // built at real size for the radius (the transform is scaled by the radius).
            ringView = ring.AddComponent<TopicRingLook>();
            ringView.Init(renderer, ringMaterial);
            ringView.Build(ringLook, ringRadius);
            ringMeshRadius = ringRadius;
            SetRingConfirmed(false);
            ringShownAt = Time.time;
            ToySounds.Play(ToySounds.Cue.Pop, center, .7f, .9f);
            ToyFx.Puff(center, .016f, 5);
        }

        // No planned formation: put the ring on the surface most characters actually sat on,
        // in the middle of them, so it is never on the floor while they stand on the desk.
        private bool TryRingNearSeats(Transform head, RoomCapsule.CapsuleRoomEnvironment room, out Vector3 center)
        {
            center = Vector3.zero;
            RoomCapsule.CapsuleRoomSurface best = null;
            int bestCount = 0;
            foreach (var surface in room.Surfaces)
            {
                int seated = 0;
                foreach (var seat in seats.Values)
                    if (surface.Contains(new Vector2(seat.x, seat.z), -.02f) && Mathf.Abs(surface.Point(new Vector2(seat.x, seat.z)).y - seat.y) < .08f) seated++;
                if (seated > bestCount) { bestCount = seated; best = surface; }
            }
            if (best == null || bestCount < 2) return false;
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var seat in seats.Values)
                if (best.Contains(new Vector2(seat.x, seat.z), -.02f)) { sum += seat; n++; }
            Vector3 middle = sum / n;
            // Pull slightly toward the user so the ring sits in front of the group.
            Vector3 toward = Vector3.ProjectOnPlane(head.position - middle, Vector3.up).normalized;
            foreach (float radius in new[] { .12f, .1f, .08f })
            foreach (float pull in new[] { .08f, .04f, 0f, .12f })
            {
                var xz = new Vector2(middle.x + toward.x * pull, middle.z + toward.z * pull);
                if (!best.Contains(xz, radius + .015f)) continue;
                center = best.Point(xz) + Vector3.up * .006f;
                if (Physics.Linecast(head.position, center + Vector3.up * .12f, room.GeometryMask, QueryTriggerInteraction.Ignore)) continue;
                ringRadius = radius;
                return true;
            }
            return false;
        }

        private bool TryFloorRingSpot(Transform head, RoomCapsule.CapsuleRoomEnvironment room, out Vector3 center)
        {
            ringRadius = .26f;
            Vector3 forward = FlatForward(head);
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            foreach (float side in new[] { 0f, -.30f, .30f, -.55f, .55f })
            foreach (float distance in new[] { .85f, .65f, 1.05f, .45f, 1.25f })
            foreach (var surface in room.Surfaces)
            {
                Vector3 point = head.position + forward * distance + right * side;
                var xz = new Vector2(point.x, point.z);
                if (!surface.Contains(xz, ringRadius + .02f)) continue;
                center = surface.Point(xz) + Vector3.up * .006f;
                if (center.y > head.position.y - .25f || !RingIsVisible(head.position, center, room.GeometryMask, ringRadius)) continue;
                return true;
            }
            center = Vector3.zero;
            return false;
        }

        private static bool RingIsVisible(Vector3 eye, Vector3 center, int geometryMask, float radius)
        {
            if (Physics.Linecast(eye, center + Vector3.up * .02f, geometryMask, QueryTriggerInteraction.Ignore)) return false;
            for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.PI / 4f;
                Vector3 rim = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius + Vector3.up * .02f;
                if (Physics.Linecast(eye, rim, geometryMask, QueryTriggerInteraction.Ignore)) return false;
            }
            return true;
        }

        // Waiting (white ring) or confirmed (mint ring; the other looks show it their own way).
        private void SetRingConfirmed(bool confirmed)
        {
            if (ringView != null) ringView.SetConfirmed(confirmed);
        }

        // Editor/tests: switch the ring's look live (it keeps its place, size and state).
        public void PreviewRingLook(RingLook look)
        {
            ringLook = look;
            if (ringView == null) return;
            bool confirmed = ringView.Confirmed;
            ringView.Build(look, ringRadius);
            ringMeshRadius = ringRadius;
            ringView.SetConfirmed(confirmed);
            ringShownAt = Time.time;
        }

        public void PreviewRingConfirmed(bool confirmed) => SetRingConfirmed(confirmed);

        // Maker: a note settled in the ring is what we are making now (confirmed; the doodle inks round it
        // again for every new note).
        public void RingNoteIn()
        {
            if (ringView != null) ringView.NoteSettled();
            if (NoteOnWall) makerSession.WiggleCurrentNote(); // F72: the note on the wall gives a little shake
        }

        public TopicRingLook RingView => ringView;

        private void LateUpdate()
        {
            AnimateRing();
            FollowMicrophone();
        }

        // Ring pops in with an overshoot, then breathes slightly while waiting for the note.
        // The ring grows with the ideas kept in it (and never into the characters).
        private float baseRingRadius = -1f, ringTarget = -1f, ringMeshRadius = -1f;
        public int RingIdeas { get; private set; }
        public void SetRingIdeas(int count)
        {
            RingIdeas = count;
            if (baseRingRadius < 0f) baseRingRadius = ringRadius;
            ringTarget = Mathf.Min(baseRingRadius + .032f * count, baseRingRadius + .18f);
        }

        private void AnimateRing()
        {
            if (TopicRing == null || !TopicRing.enabled) return;
            if (baseRingRadius < 0f) baseRingRadius = ringRadius;
            if (ringTarget > 0f && !Mathf.Approximately(ringRadius, ringTarget))
            {
                ringRadius = Mathf.MoveTowards(ringRadius, ringTarget, Time.deltaTime * .12f);
                if (ringMeshRadius < 0f) ringMeshRadius = baseRingRadius;
                // Keep the look's real sizes as it grows: rebuild it for the new size now and then.
                if (Mathf.Abs(ringRadius / ringMeshRadius - 1f) > .12f && ringView != null)
                {
                    ringView.Build(ringLook, ringRadius);
                    ringMeshRadius = ringRadius;
                }
            }
            float t = Mathf.Clamp01((Time.time - ringShownAt) / .5f) - 1f;
            const float c1 = 1.70158f, c3 = c1 + 1f;
            float pop = 1f + c3 * t * t * t + c1 * t * t;
            float breathe = CurrentPhase == Phase.ReviewingTopic ? 1f + .025f * Mathf.Sin(Time.time * 3f) : 1f;
            TopicRing.transform.localScale = Vector3.one * Mathf.Max(.001f, ringRadius * pop * breathe);
        }

        // The push-to-talk mic stands on the surface the characters stand on (the person's desk) when it is
        // within reach, else on another real surface by the person (never the floor unless the crew is on
        // the floor), as close to the person as it can be without touching a character, the topic ring,
        // something made, the real object being measured or anything Spark saw; with no surface it floats at
        // the characters' feet height, close by and clear of them. World-locked: it stays put while the
        // person looks around and is only re-placed (gliding) when they walk away (0.9 m) or something ends
        // up on its spot. The person can also pick it up (pinch its body) and put it down anywhere: over a
        // surface it settles on it, in the air it stays; moved by hand it stays until they walk 1.5 m away.
        public const float MicBase = .043f;   // the button's centre above the mic's base (ToyMicrophoneButton, scale 2.2)
        private const float MicClear = .06f;  // the mic's own radius, plus a little air
        public const float MicTouch = .045f;  // the flat puck only: a fingertip this close to its centre presses it (the designed mic: MicPressDistance)
        private bool micTouchPressing;
        private float micTouchSince = -1f;
        public int MicTouchPresses { get; private set; }
        private Vector3 micPlacedFrom;
        private Vector3 micTarget;
        private Quaternion micTargetRotation;
        private float micCheckAt;
        private Hand micHand;
        private Vector3 micGrabOffset;
        private readonly Dictionary<Hand, bool> micPinchWas = new Dictionary<Hand, bool>();
        public bool MicHeld => micHand != null || micHeldInEditor;
        public Hand MicHand => micHand; // F66: a hand carrying the mic presses nothing (MakerSession.HeldBy)
        public bool MicPlacedByHand { get; private set; }
        private bool micHeldInEditor;
        public bool MicOnSurface { get; private set; }
        public Vector3 MicPosition => microphoneButton != null ? microphoneButton.transform.position : Vector3.zero;
        // F51: where the mic stands once it has finished gliding (things placed meanwhile must keep clear of that).
        public Vector3 MicRest => microphoneButton == null ? Vector3.zero : micMoving && !MicHeld ? micTarget : microphoneButton.transform.position;
        public bool MicShown => microphoneButton != null && microphoneButton.activeInHierarchy;
        // F51: something that cannot go elsewhere needs the mic's spot: it finds itself a new one (also when
        // the person had put it there).
        public void MicMakeRoom() { MicPlacedByHand = false; micCheckAt = 0f; }

        private void FollowMicrophone()
        {
            if (microphoneButton == null || !microphoneButton.activeInHierarchy || handoff.meeting.Head == null) return;
            UpdateMicGrab();
            UpdateMicTouch();
            if (MicHeld) return;
            if (microphone != null && (microphone.IsHolding || microphone.IsVoiceBusy)) return;
            var head = handoff.meeting.Head;
            if (!micMoving)
            {
                Vector3 walked = Vector3.ProjectOnPlane(head.position - micPlacedFrom, Vector3.up);
                bool crowded = false;
                if (!MicPlacedByHand && Time.time > micCheckAt)
                {
                    micCheckAt = Time.time + .5f;
                    crowded = MicCrowded(microphoneButton.transform.position - Vector3.up * MicBase);
                }
                if (walked.magnitude < (MicPlacedByHand ? 1.5f : .9f) && !crowded) return;
                MicrophonePose(head, out micTarget, out micTargetRotation);
                micPlacedFrom = head.position;
                micMoving = true;
                MicPlacedByHand = false;
            }
            var current = microphoneButton.transform;
            float blend = 1f - Mathf.Exp(-Time.deltaTime * 4f);
            var position = Vector3.Lerp(current.position, micTarget, blend);
            var facing = Quaternion.Slerp(current.rotation, micTargetRotation, blend);
            current.SetPositionAndRotation(position, facing);
            if (microphoneCube != null) microphoneCube.transform.SetPositionAndRotation(position, facing);
            if (Vector3.Distance(position, micTarget) < .005f && Quaternion.Angle(facing, micTargetRotation) < 1f) micMoving = false;
        }

        // ------------------------------------------------------------ the mic in the hand

        private static Vector3 PinchPointOf(Hand hand)
        {
            bool thumb = HandIO.Joint(hand, HandJointId.HandThumbTip, out Pose a);
            bool index = HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose b);
            return thumb && index ? (a.position + b.position) * .5f : index ? b.position : a.position;
        }

        // The middle of the mic's body (the button sits on top of it).
        private Vector3 MicBody => microphoneButton.transform.position - Vector3.up * (MicBase * .5f);
        // F66: within MicGrabReach of the mic itself (it was a 7 cm ball round its middle, wider than any other grab).
        public const float MicGrabReach = .04f, MicGrabWins = .8f, MicThumbOnBody = .012f;
        public bool MicNear(Vector3 point) => MicShown && !float.IsInfinity(point.x) && MicPressDistance(point, out _) <= MicGrabReach;
        public Vector3 MicClosest(Vector3 point) => MicToy != null && MicToy.HasModel ? MicToy.ClosestPoint(point) : microphoneButton.transform.position;
        public int MicGrabsOverPress { get; private set; } // presses dropped because the same hand pinched the body
        public int MicGrabs { get; private set; }

        private bool MicPointerBlocked()
        {
            if (microphoneButton == null) return false;
            // F66: with hands, the designed mic is pressed by a fingertip on its top only (UpdateMicTouch). The
            // canvas behind it is 11 cm wide and covers the body a hand reaches for, so it takes no poke or ray on
            // the headset (the Editor mouse still presses it).
            if (MicToy != null && MicToy.HasModel && (!Application.isEditor || UnityEngine.XR.XRSettings.isDeviceActive)) return true;
            if (makerSession != null && makerSession.Begun && makerSession.HandsBusy) return true;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Pinching(hand)) continue;
                bool thumb = HandIO.Joint(hand, HandJointId.HandThumbTip, out Pose a), index = HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose b);
                Vector3 pinch = thumb && index ? (a.position + b.position) * .5f : index ? b.position : a.position;
                if (Vector3.Distance(pinch, MicBody) > .06f) return true;
            }
            return false;
        }

        // Touching the mic with a pointing fingertip holds it down (2026-11-11: no need to poke the exact spot).
        // F46 (2026-11-12; on the Quest presses were missed and a slip ended the recording):
        // - measured against the mic itself (ToyMicrophoneButton.PressDistance), not a 4.5 cm ball round the middle
        //   of its body 3.9 cm below the button, so a light touch on the button on top (or the icon in front) presses;
        // - it starts with a pointing finger (not pinching, the thumb MicThumbClear away) within MicPressReach of
        //   the top or the front, held there MicTouchDwell, so reaching in to pinch the body still picks it up;
        // - once down it stays down while any fingertip is within MicKeep of the mic, whatever the thumb does (a
        //   pinch flicker or a relaxing hand no longer lets go); a pressing hand that loses tracking keeps it
        //   MicTrackLost;
        // - letting go goes through WindupsMicrophone.ReleaseGrace, and a fingertip back on the mic within it
        //   carries on with the same recording; a poke or ray holding it and this finger are separate holders, so
        //   one letting go does not end what the other still holds;
        // - pressed while the last words are being written down, the mic answers "One sec…" (it used to say nothing).
        // Hands go through HandIO so Editor checks can use synthetic hands (AgentScripts/VerifyMicPress).
        // F67: MicPressReach .015 -> .02, MicTouchDwell .1 -> .04 (a quick touch on the top presses).
        // F68 (Quest 2026-11-14: "pressing the mic is still awkward", hold-to-talk kept): it presses from 3 cm, and once
        // down the whole hand keeps it (palm, any fingertip; a hand resting on the mic while talking) within MicKeep 6 cm.
        public const float MicPressReach = .03f, MicKeep = .06f, MicThumbClear = .035f, MicTouchDwell = .04f, MicTrackLost = .8f;
        private static readonly HandJointId[] MicKeepJoints = { HandJointId.HandIndexTip, HandJointId.HandMiddleTip, HandJointId.HandThumbTip, HandJointId.HandRingTip, HandJointId.HandPalm };
        public const string MicTouchHolder = "touch";
        public bool MicTouchPressing => micTouchPressing;
        private Handedness micTouchSide;
        private float micTouchLostSince = -1f;
        private bool micTouchNeedsLeave;
        private float micTouchBeganAt = -10f;
        private ToyMicrophoneButton micToy;
        private ToyMicrophoneButton MicToy => micToy != null || microphoneButton == null ? micToy : (micToy = microphoneButton.GetComponentInChildren<ToyMicrophoneButton>(true));

        // Metres from the mic (0 touching or inside it); `face`: over its top or in front of it.
        public float MicPressDistance(Vector3 point, out bool face)
        {
            var toy = MicToy;
            if (toy != null && toy.HasModel) return toy.PressDistance(point, out face);
            // The flat puck (no designed mic): the old ball round the button.
            face = true;
            return Mathf.Max(0f, Vector3.Distance(point, microphoneButton.transform.position) - (MicTouch - MicPressReach));
        }

        private void UpdateMicTouch()
        {
            if (microphone == null) return;
            float nearest = float.MaxValue, nearestTop = float.MaxValue;
            bool pointing = false, sideTracked = false;
            Handedness pointingSide = Handedness.Right, nearestSide = Handedness.Right;
            if (!MicHeld)
                foreach (var hand in HandSet.Both())
                {
                    if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip)) continue;
                    if (hand.Handedness == micTouchSide) sideTracked = true;
                    float distance = MicPressDistance(tip.position, out bool face);
                    if (MicButtonOnly && MicToy != null && MicToy.HasModel)
                    {
                        float onButton = MicToy.ButtonDistance(tip.position, out bool overButton); // F103: the glow is for the button
                        if (overButton && onButton < nearestTop) nearestTop = onButton;
                    }
                    else if (face && distance < nearestTop) nearestTop = distance;
                    // F68: keeping it down (and coming back within the grace) counts the whole hand, not only the index tip.
                    float keep = distance;
                    foreach (var joint in MicKeepJoints)
                        if (HandIO.Joint(hand, joint, out Pose part)) keep = Mathf.Min(keep, MicPressDistance(part.position, out _));
                    if (keep < nearest) { nearest = keep; nearestSide = hand.Handedness; }
                    // F67 (Quest: "the record button barely presses"): any fingertip on the button presses it - pointing,
                    // relaxed, or pinching on top of it (the user pinched the button); the thumb no longer matters. A
                    // hand at the body (not over the top) is a grab (UpdateMicGrab).
                    // F103 (Quest 2026-11-16, the user: "only pressing the button presses it; everything else is grab and
                    // release"): the index tip on the button itself (ButtonDistance, MicButtonReach), the thumb away from
                    // it (MicPokeThumb) and no pinch. A hand reaching in to pick the mic up - from the top as well - no
                    // longer starts a recording.
                    if (MicButtonOnly)
                    {
                        if (pointing || !ButtonTouch(hand, tip.position)) continue;
                    }
                    else if (pointing || distance > MicPressReach || !face) continue;
                    if (makerSession != null && !makerSession.MayPressMic(hand)) continue; // F60: a hand holding something presses nothing
                    pointing = true;
                    pointingSide = hand.Handedness;
                }
            var toy = MicToy;
            // F66: the button glows only for a fingertip coming at the top (a hand at the body gets the grab dot instead).
            if (toy != null) toy.Hover = micTouchPressing || MicHeld ? 0f : Mathf.Clamp01(1f - (nearestTop - MicPressReach) / .05f);

            if (micTouchPressing)
            {
                if (nearest <= MicKeep) { micTouchLostSince = -1f; return; }
                if (!sideTracked && !MicHeld)
                {
                    if (micTouchLostSince < 0f) micTouchLostSince = Time.time;
                    if (Time.time - micTouchLostSince < MicTrackLost) return;
                }
                micTouchPressing = false;
                micTouchNeedsLeave = true;
                micTouchLostSince = -1f;
                microphone.EndPress(MicTouchHolder, MicHeld ? "picked up" : sideTracked ? "finger left" : "hand not tracked");
                return;
            }
            // Let go a moment ago (by touch, poke or ray) and a fingertip is back on the mic: the same recording goes on.
            if (microphone.IsReleasing && nearest <= MicKeep)
            {
                micTouchSince = -1f;
                if (microphone.BeginPress(MicTouchHolder)) { micTouchPressing = true; micTouchNeedsLeave = false; micTouchSide = nearestSide; }
                return;
            }
            // After a press the finger must leave the mic before it can press again.
            if (micTouchNeedsLeave)
            {
                micTouchSince = -1f;
                if (nearest > MicPressReach + .03f) micTouchNeedsLeave = false;
                return;
            }
            if (!pointing) { micTouchSince = -1f; return; }
            if (micTouchSince < 0f) micTouchSince = Time.time;
            if (Time.time - micTouchSince < MicTouchDwell) return;
            micTouchSince = -1f;
            // Also when a poke or ray already holds it: this finger then keeps it down if that one lets go.
            if (microphone.BeginPress(MicTouchHolder))
            {
                micTouchPressing = true;
                micTouchSide = pointingSide;
                micTouchBeganAt = Time.time;
                MicTouchPresses++;
            }
            else micTouchNeedsLeave = true;   // busy: one "One sec…" per touch
        }

        // F103: the press. MicButtonOnly = false keeps F67/F68 (the top 30 % of the mic within 3 cm, pinches included).
        public static bool MicButtonOnly = true;
        public const float MicButtonReach = .012f, MicPokeThumb = .02f;
        public int MicPressesRefused { get; private set; } // checks: a fingertip by the button that did not press (pinching, thumb close)
        private bool ButtonTouch(Hand hand, Vector3 tip)
        {
            var toy = MicToy;
            float d = toy != null && toy.HasModel ? toy.ButtonDistance(tip, out bool over) : MicPressDistance(tip, out over);
            if (d > MicButtonReach || !over) return false;
            if (HandIO.RawPinching(hand)) { MicPressesRefused++; return false; }
            if (HandIO.Joint(hand, HandJointId.HandThumbTip, out Pose thumb) && Vector3.Distance(thumb.position, tip) < MicPokeThumb) { MicPressesRefused++; return false; }
            return true;
        }

        // F67: the pinch (or that hand's index tip) is on the button on top.
        private bool OnMicButton(Hand hand, Vector3 pinch)
        {
            if (MicButtonOnly) return false; // F103: a pinch is a grab, wherever it is on the mic
            if (MicPressDistance(pinch, out bool face) <= MicPressReach && face) return true;
            return HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip) && MicPressDistance(tip.position, out bool tipFace) <= MicPressReach && tipFace;
        }

        private void UpdateMicGrab()
        {
            if (micHand != null)
            {
                if (!micHand.isActiveAndEnabled || !HandIO.Connected(micHand) || !HandIO.Pinching(micHand)) { DropMic(); return; }
                SetMicPose(PinchPointOf(micHand) + micGrabOffset);
                return;
            }
            if (micHeldInEditor) return;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) continue;
                bool pinching = HandIO.Pinching(hand);
                micPinchWas.TryGetValue(hand, out bool before);
                micPinchWas[hand] = pinching;
                if (!pinching || before) continue;
                Vector3 pinch = PinchPointOf(hand);
                if (!MicNear(pinch)) continue;
                if (makerSession != null && !makerSession.MayGrabMic(hand, pinch)) continue; // F66: a busy hand, or something nearer
                // F67: a pinch on the button (over the top) presses it (UpdateMicTouch); the body is where it is picked up.
                if (OnMicButton(hand, pinch)) continue;
                // F66: a pinch on the body wins over a press that only just began with the same hand (the finger
                // brushed the button on its way): that recording is dropped. A press held longer is someone talking.
                if (microphone != null && microphone.IsHolding)
                {
                    // (A pinch flicker of the finger that holds the button down is over the top: that is still the press.)
                    MicPressDistance(pinch, out bool overTop);
                    if (MicButtonOnly) overTop = false; // F103: a pinch is never the press, over the top too
                    bool brushed = !overTop && micTouchPressing && hand.Handedness == micTouchSide && Time.time - micTouchBeganAt < MicGrabWins;
                    if (!brushed) continue;
                    microphone.Cancel();
                    micTouchPressing = false;
                    micTouchNeedsLeave = true;
                    micTouchLostSince = -1f;
                    MicGrabsOverPress++;
                }
                MicGrabs++;
                micHand = hand;
                HandIO.BeginHold(hand); // F67: grab & release
                micGrabOffset = microphoneButton.transform.position - pinch;
                micMoving = false;
                ToySounds.Play(ToySounds.Cue.Pop, pinch, .45f, 1.2f);
                break;
            }
        }

        // Carried, the mic keeps the way it faces (it never turns with the head); it turns to the person once,
        // when it is let go (DropMic).
        private void SetMicPose(Vector3 position)
        {
            Quaternion rotation = microphoneButton.transform.rotation;
            microphoneButton.transform.SetPositionAndRotation(position, rotation);
            if (microphoneCube != null) microphoneCube.transform.SetPositionAndRotation(position, rotation);
        }

        // Let go: over a surface (within 60 cm below) it settles on it; otherwise it stays in the air.
        private void DropMic()
        {
            if (micHand != null) HandIO.EndHold(micHand);
            micHand = null;
            micHeldInEditor = false;
            MicPlacedByHand = true;
            micPlacedFrom = handoff.meeting.Head.position;
            Vector3 foot = microphoneButton.transform.position - Vector3.up * MicBase;
            int mask = handoff.meeting.environment.GeometryMask;
            if (Physics.Raycast(new Ray(foot + Vector3.up * .02f, Vector3.down), out var hit, .62f, mask, QueryTriggerInteraction.Ignore) && hit.normal.y > .85f)
            {
                micTarget = hit.point + Vector3.up * MicBase;
                micTargetRotation = Quaternion.LookRotation(micTarget - handoff.meeting.Head.position, Vector3.up);
                micMoving = true;
                MicOnSurface = true;
            }
            else
            {
                // Stays in the air where it was let go, turning once to face the person.
                micTarget = microphoneButton.transform.position;
                micTargetRotation = Quaternion.LookRotation(micTarget - handoff.meeting.Head.position, Vector3.up);
                micMoving = true;
                MicOnSurface = false;
            }
            ToySounds.Play(ToySounds.Cue.Press, foot, .4f, 1.4f);
        }

#if UNITY_EDITOR
        // Editor tests: carry the mic to a point and let go there.
        public void PreviewCarryMic(Vector3 buttonPosition)
        {
            micHeldInEditor = true;
            SetMicPose(buttonPosition);
            DropMic();
        }
#endif

        // Everything the mic must keep clear of: (where it stands, how far it reaches sideways).
        // `settled`: only what stays put (a character walking past is no reason to move the mic).
        private List<(Vector3 at, float radius)> MicBusy(bool settled = false)
        {
            var busy = new List<(Vector3, float)>();
            foreach (var dock in handoff.docks)
                if (dock != null && dock.character != null && dock.character.movement != null && dock.character.movement.isActiveAndEnabled &&
                    !(settled && dock.character.movement.State == CapsulePetController.MotionState.Walking))
                    busy.Add((dock.character.movement.transform.position, .06f));
            if (TopicRing != null && TopicRing.enabled) busy.Add((TopicRing.transform.position, RingRadius + .02f));
            if (maker && makerSession != null) busy.AddRange(makerSession.Occupied());
            return busy;
        }

        private bool MicCrowded(Vector3 foot)
        {
            foreach (var (at, radius) in MicBusy(true))
                if (Mathf.Abs(at.y - foot.y) < .25f && Vector3.ProjectOnPlane(at - foot, Vector3.up).magnitude < radius + MicClear - .02f) return true;
            return false;
        }

        private static bool Clear(Vector3 foot, List<(Vector3 at, float radius)> busy)
        {
            foreach (var (at, radius) in busy)
                if (Mathf.Abs(at.y - foot.y) < .25f && Vector3.ProjectOnPlane(at - foot, Vector3.up).magnitude < radius + MicClear) return false;
            return true;
        }

        private void MicrophonePose(Transform head, out Vector3 position, out Quaternion rotation)
        {
            Vector3 forward = FlatForward(head);
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            // Where a hand rests comfortably: a little ahead and to the right.
            Vector3 reach = head.position + forward * .32f + right * .14f;
            var busy = MicBusy();
            int mask = handoff.meeting.environment.GeometryMask;
            float best = float.MaxValue;
            Vector3 foot = Vector3.zero;
            bool found = false;
            // The crew's surface first (a wider search), then any other surface that is not the floor.
            float crew = CrewFeetHeight(head);
            float floor = handoff.meeting.environment.Room != null ? handoff.meeting.environment.Room.GetRoomBounds().min.y : head.position.y - 1.6f;
            bool crewOnFloor = crew < floor + .2f;
            for (int pass = crewOnFloor ? 1 : 0; pass < 2 && !found; pass++)
                for (float ahead = .15f; ahead <= (pass == 0 ? .9f : .65f); ahead += .05f)
                    for (float side = (pass == 0 ? -.6f : -.35f); side <= (pass == 0 ? .7f : .5f); side += .05f)
                    {
                        Vector3 above = head.position + forward * ahead + right * side;
                        if (!Physics.Raycast(new Ray(above, Vector3.down), out var hit, 2.2f, mask, QueryTriggerInteraction.Ignore) || hit.normal.y < .9f) continue;
                        if (hit.point.y > head.position.y - .15f) continue;
                        bool onFloor = hit.point.y < floor + .2f;
                        if (pass == 0 && Mathf.Abs(hit.point.y - crew) > .06f) continue;   // the crew's desk
                        // Never the floor (out of reach); a surface comfortably below the eyes is best.
                        if (pass == 1 && (onFloor || hit.point.y < head.position.y - 1.2f)) continue;
                        if (!FlatUnder(hit.point, mask) || !Clear(hit.point, busy)) continue;
                        float score = Vector3.ProjectOnPlane(hit.point - reach, Vector3.up).magnitude + (pass == 1 ? Mathf.Abs(hit.point.y - (head.position.y - .5f)) * .5f : 0f);
                        // Desks, shelves and the like before the top of a monitor or a box.
                        var anchor = hit.collider.GetComponentInParent<Meta.XR.MRUtilityKit.MRUKAnchor>();
                        if (anchor != null && !anchor.HasAnyLabel(CapsuleRoomEnvironment.WalkableLabels)) score += .3f;
                        if (score < best) { best = score; foot = hit.point; found = true; }
                    }
            if (!found)
            {
                // No surface in reach: in the air at the characters' feet height (hand height when they are
                // on the floor).
                float height = crewOnFloor ? head.position.y - .45f : crew;
                for (float ahead = .2f; ahead <= .6f; ahead += .05f)
                    for (float side = -.3f; side <= .45f; side += .05f)
                    {
                        Vector3 p = head.position + forward * ahead + right * side;
                        p.y = height;
                        if (!Clear(p, busy) || Physics.CheckSphere(p + Vector3.up * .035f, .035f, mask, QueryTriggerInteraction.Ignore)) continue;
                        float score = Vector3.ProjectOnPlane(p - reach, Vector3.up).magnitude;
                        if (score < best) { best = score; foot = p; found = true; }
                    }
                if (!found) { foot = head.position + forward * .4f + right * .2f; foot.y = height; }
                MicOnSurface = false;
            }
            else MicOnSurface = true;
            position = foot + Vector3.up * MicBase;
            rotation = Quaternion.LookRotation(position - head.position, Vector3.up);
        }

        // The mic's whole base is on the surface (not hanging over an edge).
        private static bool FlatUnder(Vector3 point, int mask)
        {
            foreach (var offset in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
            {
                Vector3 from = point + offset * .035f + Vector3.up * .05f;
                if (!Physics.Raycast(new Ray(from, Vector3.down), out var hit, .1f, mask, QueryTriggerInteraction.Ignore) || Mathf.Abs(hit.point.y - point.y) > .01f) return false;
            }
            return true;
        }

        private float CrewFeetHeight(Transform head)
        {
            var feet = new List<float>();
            foreach (var dock in handoff.docks)
                if (dock != null && dock.character != null && dock.character.movement != null && dock.character.movement.isActiveAndEnabled)
                    feet.Add(dock.character.movement.transform.position.y);
            feet.Sort();
            float height = feet.Count > 0 ? feet[feet.Count / 2] : head.position.y - MicDropWithoutCrew;
            return Mathf.Clamp(height, head.position.y - 1.1f, head.position.y - .2f);
        }

        private const float MicDropWithoutCrew = .3f;

        private void OnTopicReleased()
        {
            // F72: confirmed while held: it goes up on the wall now.
            if (postWhenReleased) { postWhenReleased = false; makerSession.PostTopicNote(TopicNote.transform, topicDraft); return; }
            // The wall pad's note: written, it goes up on the wall; blank, it stays where it was left.
            if (PadNote == PadNoteState.Written) { StickWrittenNote(); return; }
            if (PadNote == PadNoteState.Peeled || PadNote == PadNoteState.OnPad) return;
            if (TopicRing == null) return;
            // No topic yet (quiet start): the blank note stays where it was let go.
            if (CurrentPhase == Phase.Meeting) { if (!TopicPending) DockTopic(); return; }
            if (CurrentPhase != Phase.ReviewingTopic) return;
            Vector3 offset = TopicNote.transform.position - TopicRing.transform.position;
            if (new Vector2(offset.x, offset.z).magnitude <= RingRadius && offset.y >= -.05f && offset.y <= .50f)
                ConfirmTopic();
        }

        private void ConfirmTopic()
        {
            if (CurrentPhase != Phase.ReviewingTopic || string.IsNullOrWhiteSpace(topicDraft)) return;
            CurrentPhase = Phase.Meeting;
            microphone.Cancel();
            if (topicTapSurface != null) topicTapSurface.raycastTarget = false;
            guide.authoredBubble.gameObject.SetActive(false);
            guide.authoredBubble.GetComponent<SpeechBubblePresentation>().UseGuideCaption(false);
            SetRingConfirmed(true);
            // F72: Maker: the note goes up on the wall (or beside Fixer) instead of into a ring, and the make begins.
            if (NoteOnWall)
            {
                ToySounds.Play(ToySounds.Cue.Chime, TopicNote.transform.position, .8f);
                ToyFx.Confetti(TopicNote.transform.position + Vector3.up * .04f, 12);
                makerSession.Begin(topicDraft);
                microphone?.ShowMeetingHint("Hold to talk to us"); // F76: the review's "Say I'm done" is over
                if (TopicNote.IsHeld) postWhenReleased = true;
                else makerSession.PostTopicNote(TopicNote.transform, topicDraft);
                return;
            }
            dockWhenReleased = TopicNote.IsHeld;
            if (!dockWhenReleased) DockTopic();
            ToySounds.Play(ToySounds.Cue.Chime, TopicNote.transform.position, .8f);
            if (TopicRing != null)
            {
                ringShownAt = Time.time - .2f; // a small re-pop as it turns mint
                ToyFx.Confetti(TopicRing.transform.position + Vector3.up * .08f, 16);
            }
            // Maker (F26): the note stays in the ring as the reminder of what we are making.
            if (maker && makerSession != null) { makerSession.Begin(topicDraft); microphone?.ShowMeetingHint("Hold to talk to us"); return; } // F76
            handoff.meeting.BeginArrivedMeeting(topicDraft, microphone);
        }

        // F30: a first-timer who stays quiet at "What do you want to make today?" gets the crew's idea meeting:
        // the session begins with nothing chosen and the crew finds ideas. The blank note stays where the person
        // left it (no ring yet, 2026-11-12); once an idea is picked or said it is written on that note and the
        // note goes into the ring (WriteTopic).
        private float topicQuietSince = -1f;
        public bool QuietMeetingStarted { get; private set; }
        public bool TopicPending => QuietMeetingStarted && CurrentPhase == Phase.Meeting && string.IsNullOrWhiteSpace(topicDraft);

        private void TopicQuietFrame()
        {
            bool previewSkipping = false;
#if UNITY_EDITOR
            previewSkipping = skipping;
#endif
            if (!maker || makerSession == null || CurrentPhase != Phase.WaitingForTopic || previewSkipping) { topicQuietSince = -1f; return; }
            bool busy = microphone != null && (microphone.IsHolding || microphone.IsCapturing) || !string.IsNullOrWhiteSpace(topicDraft);
            if (topicQuietSince < 0f || busy) { topicQuietSince = Time.time; return; }
            if (Time.time - topicQuietSince < MakerSession.StuckSilence) return;
            topicQuietSince = -1f;
            makerSession.IdeasQuiet(); // F45: the crew never looks around unasked; the Find ideas pill bounces once
        }

        public const string OpeningQuestion = "What do you want to make?\nTell me with the mic, or we can\nlook around your room for ideas!";

        // F45: the person pressed Find ideas (or asked out loud) at the first note.
        public void BeginQuietMeeting(string trigger = "button")
        {
            if (!maker || makerSession == null || CurrentPhase != Phase.WaitingForTopic) return;
            QuietMeetingStarted = true;
            topicDraft = "";
            CurrentPhase = Phase.Meeting;
            microphone?.Cancel();
            if (topicTapSurface != null) topicTapSurface.raycastTarget = false;
            guide.authoredBubble.gameObject.SetActive(false);
            guide.authoredBubble.GetComponent<SpeechBubblePresentation>().UseGuideCaption(false);
            dockWhenReleased = false; // the note stays where it is until there is a topic
            makerSession.HideIdeasButton();
            makerSession.BeginQuiet(trigger);
            Debug.Log("[Windups Arrival] Find ideas at the topic (" + trigger + "): the crew looks around for ideas.");
        }

        // The topic is decided (a picked idea note, or said): it is written on the waiting note, the ring shows
        // and the note goes into it (held: when let go).
        public void WriteTopic(string text)
        {
            if (!QuietMeetingStarted || !string.IsNullOrWhiteSpace(topicDraft) || string.IsNullOrWhiteSpace(text)) return;
            topicDraft = text.Trim();
            TopicNote.SetText("Me", topicDraft, topicDraft);
            // F72: Maker: the written note goes up on the wall (held: when let go).
            if (NoteOnWall)
            {
                if (TopicNote.IsHeld) postWhenReleased = true;
                else makerSession.PostTopicNote(TopicNote.transform, topicDraft);
                Debug.Log("[Windups Arrival] Topic decided after the idea meeting: " + topicDraft);
                return;
            }
            ShowTopicRing();
            SetRingConfirmed(true);
            dockWhenReleased = TopicNote.IsHeld;
            if (!dockWhenReleased) StartCoroutine(FlyTopicToRing());
            Debug.Log("[Windups Arrival] Topic decided after the idea meeting: " + topicDraft);
        }

        public bool TopicFlying { get; private set; }

        // From where it waited into the ring, in a little hop; grabbing it on the way stops the flight
        // (it docks when let go).
        private IEnumerator FlyTopicToRing()
        {
            if (TopicRing == null) { DockTopic(); yield break; }
            TopicFlying = true;
            var note = TopicNote.transform;
            Vector3 from = note.position;
            Quaternion fromRotation = note.rotation;
            ToySounds.Play(ToySounds.Cue.Paper, from, .7f, 1.2f);
            for (float t = 0f; t < 1f; t += Time.deltaTime / .7f)
            {
                if (TopicNote.IsHeld || TopicRing == null) { dockWhenReleased = TopicNote.IsHeld; TopicFlying = false; yield break; }
                Vector3 to = DockedNotePosition;
                Vector3 away = Vector3.ProjectOnPlane(to - handoff.meeting.Head.position, Vector3.up);
                if (away.sqrMagnitude < .0001f) away = Vector3.forward;
                var toRotation = Quaternion.LookRotation(away.normalized, Vector3.up) * Quaternion.Euler(22f, 0f, 0f);
                float e = Mathf.SmoothStep(0f, 1f, t);
                note.SetPositionAndRotation(Vector3.Lerp(from, to, e) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .08f, Quaternion.Slerp(fromRotation, toRotation, e));
                yield return null;
            }
            TopicFlying = false;
            DockTopic();
            ToySounds.Play(ToySounds.Cue.Chime, note.position, .8f);
            ringShownAt = Time.time - .2f; // a small re-pop as the note lands
            ToyFx.Confetti(TopicRing.transform.position + Vector3.up * .08f, 16);
        }

        // Start over: the guide offers a fresh topic note again.
        public void RestartTopic()
        {
            topicDraft = "";
            QuietMeetingStarted = false;
            topicTaken = false;
            dockWhenReleased = false;
            if (TopicRing != null) { TopicRing.enabled = false; SetRingConfirmed(false); }
            guide.authoredBubble.GetComponent<SpeechBubblePresentation>().UseGuideCaption();
            microphone?.ResetArrivalFeedback();
            OfferTopicNote();
        }

        // F98 (the user: after a finished work, "Make another" should bring a new post-it, and then speaking, finding
        // ideas (Spark's magnifier) and so on work exactly as the first time): the guide holds out a fresh note again and
        // the arrival's first-time steps run (take it, Find ideas or the mic, review, confirm -> Begin). The note was the
        // pad's top sheet: it leaves the pad (the pad gets a fresh one when this note goes up).
        public int NewNotesOffered { get; private set; }
        public void OfferNewNote()
        {
            if (!maker || makerSession == null) { RestartTopic(); return; }
            PadNote = PadNoteState.None;
            postWhenReleased = false;
            NewNotesOffered++;
            RestartTopic();
        }

        // In the ring the topic shrinks to a small table card, tilted up toward the user,
        // so the six characters behind it stay visible.
        public const float DockedNoteWidth = .095f, DockedNoteHeight = .09f;
        public Vector3 DockedNotePosition => TopicRing == null ? Vector3.zero
            : TopicRing.transform.position + Vector3.up * (DockedNoteHeight * .5f + RingRest) +
              (maker && makerSession != null ? makerSession.RingNoteOffset(TopicRing.transform.position, RingRadius) : Vector3.zero); // F68: off Maker's line
        private void DockTopic()
        {
            dockWhenReleased = false;
            if (TopicRing == null) return;
            if (topicPaper != null)
            {
                topicPaper.openWidth = DockedNoteWidth;
                topicPaper.openHeight = DockedNoteHeight;
                topicPaper.OpenFully();
            }
            Vector3 position = DockedNotePosition;
            Vector3 away = Vector3.ProjectOnPlane(position - handoff.meeting.Head.position, Vector3.up);
            if (away.sqrMagnitude < .0001f) away = Vector3.forward;
            TopicNote.transform.SetPositionAndRotation(position,
                Quaternion.LookRotation(away.normalized, Vector3.up) * Quaternion.Euler(22f, 0f, 0f));
            ToyFx.Puff(position, .012f, 4);
        }

        public void TopicNoteTapped()
        {
            CountTopicTap(Time.unscaledTime);
        }

        private void CountTopicTap(float now)
        {
            if (CurrentPhase != Phase.ReviewingTopic) return;
            if (NoteOnWall) return; // F72: with Maker only the note's Rewrite button clears it
            // One tap (poke, far pinch-tap or click) clears the note to say it again. A far
            // pinch can report the same tap through both the UI ray and RemoteGrab: count it once.
            if (now - firstTopicTap < .3f) return;
            firstTopicTap = now;
            ClearTopicNote();
        }

        // F72: the note's buttons in review (MakerSession.WallItems.cs): Rewrite clears the words (the person presses the
        // mic and says it again); the green check is "I'm done".
        public void RewriteTopicNote()
        {
            if (CurrentPhase != Phase.ReviewingTopic) return;
            ClearTopicNote();
        }

        public void PressTopicDone()
        {
            if (CurrentPhase == Phase.ReviewingTopic) ConfirmTopic();
        }

        private void ClearTopicNote()
        {
            TopicNote.SetText("Me", "");
            if (TopicRing != null) TopicRing.enabled = false;
            if (topicTapSurface != null) topicTapSurface.raycastTarget = false;
            if (pushToTalk != null) pushToTalk.enabled = true;
            microphone?.ResetArrivalFeedback();
            CurrentPhase = Phase.WaitingForTopic;
            ShowGuide("Let's try that again.\nHold the mic and say it.");
            if (maker && makerSession != null) makerSession.ShowIdeasButton(); // F45
            handoff.meeting.status.text = "Hold the microphone and say it again.";
            Debug.Log("[Windups Arrival] Topic revision requested.");
        }

        private bool TryAssignSeat(CapsulePetController pet, List<Vector3> candidates, float maximumPath)
        {
            // Formation seats are planned to sit close around the ring.
            float spacing = formation != null && CurrentPhase == Phase.Gathering ? .085f : .30f;
            foreach (var point in candidates)
            {
                bool occupied = false;
                foreach (var reserved in seats.Values)
                    if (Vector3.Distance(reserved, point) < spacing) { occupied = true; break; }
                if (occupied || !pet.GoTo(point, maximumPath)) continue;
                seats[pet] = point;
                return true;
            }
            return false;
        }

        private static Vector3 FlatForward(Transform head)
        {
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            return forward.sqrMagnitude > .001f ? forward.normalized : Vector3.forward;
        }

        private void OnApplicationPause(bool value)
        {
            paused = value;
            clap.Reset();
        }

        private void OnDestroy()
        {
            if (TopicNote != null) TopicNote.Released -= OnTopicReleased;
            if (ringMaterial != null) Destroy(ringMaterial);
            if (microphone != null)
            {
                microphone.TranscriptUpdated -= HandleTranscriptUpdate;
                microphone.TranscriptReady -= HandleTranscript;
            }
        }

        // `speaker`: whose line it is (the introduction's teammate); its mumble is that teammate's voice (F79).
        private void ShowGuide(string message, WindupCharacter speaker = null)
        {
            var bubble = guide.authoredBubble;
            // F52: the words first, then the card (shown first, it popped up with its old text for a moment).
            bubble.SetText(guide.characterName, message, "");
            bubble.title.text = message;
            bubble.gameObject.SetActive(true);
            if (!bubble.IsHeld && bubble.startingDock != null && bubble.Owner != bubble.startingDock)
                bubble.startingDock.Attach(bubble);
            WindupBody.Express(guide, message, 2.5f);
            // F79 (Quest 2026-11-14: "no sound in the introduction and before the ideas - there should be, every bubble"):
            // these lines come before the maker session's own captions, which mumble; now they mumble too.
            if (maker && makerSession != null) makerSession.MumbleLine((speaker ?? guide).characterName, message);
        }
    }
}
