using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using Windups.BubbleLab;

namespace Windups.Handoff
{
    // Character speech-card interactions during the arrival meeting (Quest hands + Editor):
    //  hover a card        -> a sticker tray appears beside it; poke a sticker to stick it on
    //  tap a card          -> hear more from that character (or present its waiting line)
    //  grab a card         -> everyone falls quiet and looks at the user
    //  pull it to the face -> it unfolds into the full sentence in larger type
    //  release elsewhere   -> it stays where it was let go (a placed copy; see .Cards.cs);
    //                         in the ring it becomes a small summary chip; over another
    //                         character it is handed over for review
    //  crumple and release -> it flies into Benefit's basket (tap the basket to shake it out)
    //  the deck behind     -> earlier lines; tap it to fan them out
    //  Decisions' notebook -> tap for the meeting notes
    // Each character has one live card that is reused for every line, so history, kept,
    // handed and discarded ideas are PaperSnapshot copies, and a card placed in the room is a
    // grabbable copy; the live card itself always pops back home with the next line.
    public sealed partial class BubbleInteractions : MonoBehaviour
    {
        public static BubbleInteractions Instance { get; private set; }
        // 👍 Like, 😍 Love, 🤔 Hmm, 👎 Nah (EmojiStickers draws them).
        public static readonly string[] StickerKinds = EmojiStickers.Names;

        public WindupsArrival arrival;

        private sealed class CardState
        {
            public TransferableSpeechBubble card;
            public WindupCharacter owner;
            public SpeechBubbleDock home;
            public TangibleBubble paper;
            public SpeechBubblePresentation look;
            public MeetingLine line;
            public string text = "", summary = "";
            public bool spoken, held, reading, editorReading;
            public Vector3 heldAt, velocity;
            public Quaternion heldRot = Quaternion.identity;
            public float heldWidth, heldHeight;
            public bool heldShowsDetail;
            public float baseScale = 1f, scale = 1f;
            public bool free, expanded, stretching; // free = a placed card that no character holds
            public float stretchScale = 1f;
            public readonly List<string> stickers = new List<string>();
            public readonly List<Transform> stuck = new List<Transform>();
        }

        private readonly List<CardState> cards = new List<CardState>();
        private readonly Dictionary<WindupCharacter, List<PaperSnapshot>> decks = new Dictionary<WindupCharacter, List<PaperSnapshot>>();
        private readonly List<PaperSnapshot> keyIdeas = new List<PaperSnapshot>();
        private readonly List<PaperSnapshot> discarded = new List<PaperSnapshot>();
        private readonly List<string> reactionLog = new List<string>();
        private readonly List<Mesh> ownedMeshes = new List<Mesh>();
        private readonly Transform[] trayButtons = new Transform[StickerKinds.Length];
        private readonly Dictionary<Hand, bool> wasPinching = new Dictionary<Hand, bool>();
        private HashSet<object> poked = new HashSet<object>();
        private GameObject tray;
        private CardState trayCard;
        private float trayUntil, quietUntil;
        private WindupCharacter fanned;
        private float fanUntil;
        private Transform basket, notebook;
        private WindupBody benefitBody;
        private float basketRevealAt;
        public const float BasketCatchFrame = 20f, BasketShakeFrom = 14f, BasketShakeTo = 34f;
        private bool basketOpen;
        private float basketShakeAt = -10f;
        private GameObject minutes;
        private TextMeshProUGUI minutesText;
        private Material material;
        private TMP_FontAsset font;
        private Hand[] hands;
        private bool anyHeldBefore;
        private float lastAction;
        private bool setup;

        public bool Active => arrival != null && arrival.CurrentPhase == WindupsArrival.Phase.Meeting && !arrival.MakerActive;
        public IReadOnlyList<PaperSnapshot> KeyIdeas => keyIdeas;
        public IReadOnlyList<PaperSnapshot> Discarded => discarded;
        public IReadOnlyList<string> ReactionLog => reactionLog;
        public bool TrayVisible => tray != null && tray.activeSelf;
        public TransferableSpeechBubble TrayCard => trayCard?.card;
        public WindupCharacter FannedCharacter => fanned;
        public bool BasketOpen => basketOpen;
        public bool MinutesOpen => minutes != null && minutes.activeSelf;
        public string MinutesText => minutesText != null ? minutesText.text : "";
        private WindupsMeeting Meeting => arrival.handoff.meeting;

        public void Initialize(WindupsArrival owner) { arrival = owner; Instance = this; }
        private void Awake() { Instance = this; }
        private void Start() { hands = FindObjectsByType<Hand>(FindObjectsInactive.Include, FindObjectsSortMode.None); }
        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            foreach (var mesh in ownedMeshes) if (mesh != null) Destroy(mesh);
        }

        // ---------------------------------------------------------------- setup

        private void EnsureSetup()
        {
            if (setup) return;
            setup = true;
            var guideCard = arrival.guide.authoredBubble;
            material = guideCard.GetComponent<MeshRenderer>().sharedMaterial;
            var guideLook = guideCard.GetComponent<SpeechBubblePresentation>();
            font = guideLook != null ? guideLook.toyFont : null;
            foreach (var dock in arrival.handoff.docks)
            {
                var card = dock.character.authoredBubble;
                if (card == null) continue;
                var state = new CardState
                {
                    card = card, owner = dock.character, home = dock,
                    paper = card.GetComponent<TangibleBubble>(), look = card.GetComponent<SpeechBubblePresentation>()
                };
                state.baseScale = 1f; // docks keep cards at scale 1 (the pop animation briefly shrinks them)
                card.Released += () => OnReleased(state);
                cards.Add(state);
                decks[dock.character] = new List<PaperSnapshot>();
            }
            tray = new GameObject("Sticker Tray");
            tray.transform.SetParent(transform, false);
            for (int i = 0; i < StickerKinds.Length; i++) trayButtons[i] = MakeSticker(StickerKinds[i], tray.transform, material, font, 1f);
            tray.SetActive(false);
            BuildBasket();
            BuildNotebook();
            BuildMinutes();
            BuildTeamThinking();
        }

        // An emoji sticker (see EmojiStickers): a flat world-space image, 4.2 cm across at scale 1.
        public static Transform MakeSticker(string kind, Transform parent, Material material, TMP_FontAsset font, float scale)
        {
            int index = Mathf.Max(0, Array.IndexOf(StickerKinds, kind));
            var root = new GameObject("Sticker " + kind, typeof(RectTransform), typeof(Canvas)).transform;
            root.gameObject.layer = parent != null ? parent.gameObject.layer : 0;
            var rect = (RectTransform)root;
            rect.SetParent(parent, false);
            rect.sizeDelta = new Vector2(42f, 42f);
            rect.localScale = Vector3.one * (.001f * scale);
            root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var image = new GameObject("Emoji", typeof(RectTransform)).AddComponent<UnityEngine.UI.Image>();
            image.gameObject.layer = root.gameObject.layer;
            image.rectTransform.SetParent(rect, false);
            image.rectTransform.anchorMin = Vector2.zero;
            image.rectTransform.anchorMax = Vector2.one;
            image.rectTransform.offsetMin = image.rectTransform.offsetMax = Vector2.zero;
            image.sprite = EmojiStickers.Get(index);
            image.preserveAspect = true;
            image.raycastTarget = false;
            return root;
        }

        private Transform MeshObject(string name, Mesh mesh, Transform parent)
        {
            ownedMeshes.Add(mesh);
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go.transform;
        }

        private Vector3 TowardUser(Vector3 from)
        {
            Vector3 flat = Vector3.ProjectOnPlane(Meeting.Head.position - from, Vector3.up);
            return flat.sqrMagnitude > .0001f ? flat.normalized : Vector3.back;
        }

        // The pet root sits on the surface it stands on.
        private static Vector3 Feet(WindupCharacter actor) => actor.movement.transform.position;

        private WindupCharacter Actor(string name)
        {
            foreach (var dock in arrival.handoff.docks) if (dock.character.characterName == name) return dock.character;
            return null;
        }

        private void BuildBasket()
        {
            var benefit = Actor("Benefit");
            if (benefit == null) return;
            basket = new GameObject("Benefit Basket").transform;
            basket.SetParent(transform, false);
            // The modelled Benefit carries its own basket: the anchor follows it, nothing else is drawn.
            benefitBody = WindupBody.Of(benefit);
            if (benefitBody != null && benefitBody.hasBasket) { PlaceBasket(); return; }
            benefitBody = null;
            var body = MeshObject("Basket", ToyMeshes.Basket(.045f, .042f, .005f, new Color(.95f, .63f, .2f), new Color(1f, .93f, .8f), new Color(1f, .82f, .5f)), basket);
            body.localPosition = Vector3.zero;
            PlaceBasket();
        }

        private void PlaceBasket()
        {
            var benefit = Actor("Benefit");
            if (basket == null || benefit == null) return;
            if (benefitBody != null) { basket.position = benefitBody.BasketPoint - Vector3.up * .018f; return; }
            Vector3 feet = Feet(benefit);
            Vector3 side = Vector3.Cross(Vector3.up, TowardUser(feet));
            basket.position = feet + TowardUser(feet) * .07f + side * .055f + Vector3.up * .002f;
        }

        private void BuildNotebook()
        {
            var decisions = Actor("Decisions");
            if (decisions == null) return;
            notebook = new GameObject("Decisions Notebook").transform;
            notebook.SetParent(transform, false);
            var cover = MeshObject("Cover", ToyMeshes.RoundedSlab(.07f, .05f, .008f, .012f, new Color(.29f, .55f, .96f), new Color(1f, .98f, .93f),
                new Color(.29f, .55f, .96f), "NotebookCover"), notebook);
            cover.localPosition = Vector3.zero;
            var canvas = new GameObject("Label", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(notebook, false);
            rect.sizeDelta = new Vector2(70f, 50f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, 0f, -.0015f);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var label = new GameObject("Text", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            label.rectTransform.SetParent(rect, false);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
            if (font != null) label.font = font;
            label.text = "Notes";
            label.fontSize = 15f;
            label.fontStyle = FontStyles.Bold;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            PlaceNotebook();
        }

        private void PlaceNotebook()
        {
            var decisions = Actor("Decisions");
            if (notebook == null || decisions == null) return;
            Vector3 feet = Feet(decisions);
            Vector3 toward = TowardUser(feet);
            notebook.position = feet + toward * .075f + Vector3.up * .012f;
            // Lying on the surface like a small lectern, cover facing up toward the user.
            notebook.rotation = Quaternion.LookRotation(Vector3.Slerp(Vector3.down, -toward, .35f), -toward);
        }

        private void BuildMinutes()
        {
            minutes = new GameObject("Meeting Notes");
            minutes.transform.SetParent(transform, false);
            var panel = MeshObject("Panel", ToyMeshes.RoundedSlab(.34f, .27f, .02f, .008f, Color.white, new Color(.88f, .88f, .92f),
                new Color(.95f, .95f, .97f), "NotesPanel"), minutes.transform);
            panel.localPosition = Vector3.zero;
            var band = MeshObject("Band", ToyMeshes.RoundedSlab(.326f, .04f, .013f, .002f, new Color(.29f, .55f, .96f),
                new Color(.25f, .47f, .82f), new Color(.29f, .55f, .96f), "NotesBand"), minutes.transform);
            band.localPosition = new Vector3(0f, .27f * .5f - .007f - .02f, -.0012f);
            var canvas = new GameObject("Text", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(minutes.transform, false);
            rect.sizeDelta = new Vector2(340f, 270f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, 0f, -.0026f);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var title = AddText(rect, "Meeting notes · Decisions", 20f, FontStyles.Bold, Color.white, TextAlignmentOptions.Left,
                new Vector2(0f, 108f), new Vector2(310f, 34f));
            minutesText = AddText(rect, "", 14f, FontStyles.Normal, new Color(.165f, .165f, .18f), TextAlignmentOptions.TopLeft,
                new Vector2(0f, -18f), new Vector2(310f, 210f));
            minutesText.enableAutoSizing = true;
            minutesText.fontSizeMin = 8f;
            minutesText.overflowMode = TextOverflowModes.Truncate; // never spill outside the panel
            minutesText.fontSizeMax = 14f;
            minutesText.textWrappingMode = TextWrappingModes.Normal;
            minutesText.richText = true;
            title.textWrappingMode = TextWrappingModes.NoWrap;
            minutes.SetActive(false);
        }

        private TextMeshProUGUI AddText(RectTransform parent, string value, float size, FontStyles style, Color color,
            TextAlignmentOptions alignment, Vector2 at, Vector2 box)
        {
            var text = new GameObject("Line", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            text.rectTransform.SetParent(parent, false);
            text.rectTransform.sizeDelta = box;
            text.rectTransform.anchoredPosition = at;
            if (font != null) text.font = font;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            return text;
        }

        // ---------------------------------------------------------------- frame

        private struct Pointing { public Ray ray; public bool press; }

        private void Update()
        {
            if (!Active)
            {
                if (tray != null && tray.activeSelf) tray.SetActive(false);
                if (teamThinking != null && teamThinking.gameObject.activeSelf) teamThinking.gameObject.SetActive(false);
                if (callout != null && callout.gameObject.activeSelf) { callout.gameObject.SetActive(false); basketGlow.gameObject.SetActive(false); }
                return;
            }
            EnsureSetup();
            TrackLines();
            UpdateHeld();
            UpdateGestures();
            ReadInput(out var tips, out var pinches, out var rays, out bool mouse, out Ray mouseRay, out bool mouseClick);
            UpdateHover(tips, rays, mouse, mouseRay);
            HandleTaps(tips, pinches, rays, mouse, mouseRay, mouseClick);
            UpdateMicrogestures();
            PokeHeldStack(tips);
            ExpireBrowsing();
            UpdateTeamThinking();
            UpdateCrumpleCall();
#if UNITY_EDITOR
            EditorKeys();
#endif
        }

        private void LateUpdate()
        {
            if (!Active || !setup) return;
            UpdateCardScales();
            LayoutTray();
            LayoutStuck();
            LayoutDecks();
            LayoutKept();
            LayoutPiles();
            HideStaleStackHint();
            LayoutBasket();
        }

        private readonly List<Vector3> tipBuffer = new List<Vector3>();
        private readonly List<bool> pinchBuffer = new List<bool>();
        private readonly List<Pointing> rayBuffer = new List<Pointing>();
        // A fingertip resting on a card: it becomes a tap when the finger leaves, unless the
        // hand pinched or grabbed meanwhile (reaching in to grab must not count as a tap).
        private sealed class CardTouch { public float since; public bool spoiled; }
        private readonly Dictionary<CardState, CardTouch> cardTouches = new Dictionary<CardState, CardTouch>();
        private readonly HashSet<CardState> touchedCards = new HashSet<CardState>();
        private readonly List<CardState> endedTouches = new List<CardState>();

        private void ReadInput(out List<Vector3> tips, out List<bool> pinches, out List<Pointing> rays,
            out bool mouse, out Ray mouseRay, out bool mouseClick)
        {
            tips = tipBuffer; tips.Clear();
            pinches = pinchBuffer; pinches.Clear();
            rays = rayBuffer; rays.Clear();
            mouse = false; mouseClick = false; mouseRay = default;
#if UNITY_EDITOR
            if (!UnityEngine.XR.XRSettings.isDeviceActive && Camera.main != null && UnityEngine.InputSystem.Mouse.current is { } device)
            {
                mouse = true;
                mouseRay = Camera.main.ScreenPointToRay(device.position.ReadValue());
                mouseClick = device.leftButton.wasPressedThisFrame;
            }
#endif
            // One Hand per side (the rig has several Hand components per physical hand).
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !hand.IsConnected) continue;
                bool pinching = hand.GetIndexFingerIsPinching();
                if (hand.GetJointPose(HandJointId.HandIndexTip, out Pose tip)) { tips.Add(tip.position); pinches.Add(pinching); }
                wasPinching.TryGetValue(hand, out bool before);
                wasPinching[hand] = pinching;
                if (hand.GetPointerPose(out Pose pointer))
                    rays.Add(new Pointing { ray = new Ray(pointer.position, pointer.forward), press = pinching && !before });
            }
        }

        // ---------------------------------------------------------------- lines and deck

        // When a line that was said (or reacted to) is replaced, it moves into the deck
        // behind the card, keeping its stickers.
        private void TrackLines()
        {
            foreach (var state in cards)
            {
                if (state.free) continue;
                var now = state.owner.CurrentLine;
                if (now == state.line) { state.spoken |= state.owner.IsPresenting; continue; }
                if (state.line != null && (state.spoken || state.stickers.Count > 0))
                    PushHistory(state.owner, state.owner.characterName, state.text, state.stickers, state.line);
                ClearStickers(state);
                SyncLine(state);
            }
        }

        private static void SyncLine(CardState state)
        {
            state.line = state.owner.CurrentLine;
            state.spoken = state.owner.IsPresenting;
            state.text = state.line == null ? "" : !string.IsNullOrWhiteSpace(state.line.body) ? state.line.body : state.line.summary ?? "";
            state.summary = state.line == null ? "" : !string.IsNullOrWhiteSpace(state.line.summary) ? state.line.summary : state.text;
        }

        private void PushHistory(WindupCharacter deckOwner, string speaker, string text, IEnumerable<string> stickers, MeetingLine line = null)
        {
            if (!decks.TryGetValue(deckOwner, out var deck)) return;
            var snapshot = PaperSnapshot.Create(transform, speaker, text, stickers, SpeechBubblePresentation.ResolveRoleColor(speaker), material, font);
            snapshot.Line = line;
            var home = DockOf(deckOwner);
            snapshot.transform.position = home != null ? home.anchor.position : deckOwner.transform.position;
            deck.Insert(0, snapshot);
            while (deck.Count > 8) { Destroy(deck[deck.Count - 1].gameObject); deck.RemoveAt(deck.Count - 1); }
        }

        private SpeechBubbleDock DockOf(WindupCharacter actor)
        {
            foreach (var dock in arrival.handoff.docks) if (dock.character == actor) return dock;
            return null;
        }

        private void ClearStickers(CardState state)
        {
            foreach (var stuck in state.stuck) if (stuck != null) Destroy(stuck.gameObject);
            state.stuck.Clear();
            state.stickers.Clear();
        }

        public int DeckCount(WindupCharacter actor) => decks.TryGetValue(actor, out var deck) ? deck.Count : 0;
        public IReadOnlyList<PaperSnapshot> Deck(WindupCharacter actor) => decks.TryGetValue(actor, out var deck) ? deck : new List<PaperSnapshot>();

        public void ToggleFan(WindupCharacter actor)
        {
            fanned = fanned == actor ? null : actor;
            fanUntil = Time.time + 15f;
            lastAction = Time.time;
            ToySounds.Play(ToySounds.Cue.Paper, actor != null ? actor.transform.position : transform.position, .7f, fanned != null ? 1.2f : .9f);
        }

        // Held up in the hand: the earlier lines stack behind the card, faded and fanned up-left
        // like a deck; a thumb swipe brings the next one to the front (solid).
        private void LayoutHeldDeck(WindupCharacter actor, List<PaperSnapshot> deck, CardState state)
        {
            var t = state.card.transform;
            ShowStackHint(state, deck.Count, BrowseIndex(actor));
            Vector3 back = t.forward, up = t.up, right = t.right;
            float width = state.paper.Width * t.lossyScale.x;
            float scale = Mathf.Clamp(width / PaperSnapshot.DefaultWidth, .6f, 2.2f) * .96f;
            int browsed = BrowseIndex(actor) - 1, behind = 0;
            bool crumpled = state.paper.IsCrumpled;
            for (int i = 0; i < deck.Count; i++)
            {
                var copy = deck[i];
                // A crumpled card is on its way to the basket: no deck behind it.
                if (crumpled) { if (copy.gameObject.activeSelf) copy.gameObject.SetActive(false); continue; }
                if (i == browsed)
                {
                    copy.gameObject.SetActive(true);
                    copy.SetFaded(false);
                    copy.SetPose(t.position - back * .012f, t.rotation, scale, true, true);
                    continue;
                }
                bool shown = behind < 3;
                if (copy.gameObject.activeSelf != shown) copy.gameObject.SetActive(shown);
                if (!shown) continue;
                int k = ++behind;
                copy.SetFaded(true);
                copy.SetPose(t.position + back * (.012f * k) + up * (.03f * k) - right * (.018f * k), t.rotation, scale * (1f - .04f * k), true, true);
            }
        }

        private void LayoutDecks()
        {
            if (fanned != null && Time.time > fanUntil) fanned = null;
            foreach (var pair in decks)
            {
                var deck = pair.Value;
                if (deck.Count == 0) continue;
                var home = DockOf(pair.Key);
                // Each deck faces the user like its character's card does.
                var facing = home.anchor.rotation;
                Vector3 back = facing * Vector3.forward, right = facing * Vector3.right;
                var state = cards.Find(c => c.owner == pair.Key && !c.free);
                if (state != null && state.card.IsHeld) { LayoutHeldDeck(pair.Key, deck, state); continue; }
                foreach (var copy in deck) copy.SetFaded(false);
                bool live = state != null && state.card.gameObject.activeInHierarchy && state.card.Owner == home && !state.card.IsHeld;
                Vector3 top = live
                    ? state.card.transform.TransformPoint(new Vector3(0f, state.paper.Height * .5f, 0f))
                    : home.anchor.position + Vector3.up * (home.lift + .02f) - back * SpeechBubbleDock.ForwardOffset;
                if (fanned == pair.Key)
                {
                    int n = Mathf.Min(5, deck.Count);
                    Vector3 center = home.anchor.position + Vector3.up * (home.lift + .2f) - back * .1f;
                    for (int i = 0; i < deck.Count; i++)
                    {
                        bool shown = i < n;
                        deck[i].gameObject.SetActive(shown);
                        if (!shown) continue;
                        float offset = i - (n - 1) * .5f;
                        deck[i].SetPose(center + right * (offset * .15f) - Vector3.up * (Mathf.Abs(offset) * .012f),
                            facing * Quaternion.Euler(0f, 0f, -offset * 4f), .95f, true);
                    }
                }
                else
                {
                    float fit = live ? Mathf.Clamp(state.paper.Width / PaperSnapshot.DefaultWidth * .94f, .4f, 1f) : .7f;
                    int browsed = live ? BrowseIndex(pair.Key) - 1 : -1;
                    for (int i = 0; i < deck.Count; i++)
                    {
                        // Swiped to: this earlier line comes to the front of the live card.
                        if (i == browsed)
                        {
                            deck[i].gameObject.SetActive(true);
                            deck[i].SetPose(state.card.transform.position - back * .025f, facing,
                                Mathf.Clamp(state.paper.Width / PaperSnapshot.DefaultWidth * 1.05f, .6f, 1.2f), true);
                            continue;
                        }
                        bool shown = i < 2;
                        deck[i].gameObject.SetActive(shown);
                        if (!shown) continue;
                        // Thin edges peeking above the live card, like a stack of earlier notes.
                        float scale = fit * (1f - .05f * i), half = PaperSnapshot.DefaultHeight * .5f * scale;
                        float lift = live ? .005f * (i + 1) - half : half + .005f * i;
                        deck[i].SetPose(top + Vector3.up * lift + back * (.006f + .005f * i), facing, scale, !live && i == 0);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- hover and stickers

        private bool Hoverable(CardState state) =>
            state.card.gameObject.activeInHierarchy && (state.free || state.card.Owner != null) && !state.card.IsHeld &&
            (state.look == null || !state.look.IsThinking) && state.paper != null && !state.paper.IsCrumpled &&
            !string.IsNullOrWhiteSpace(state.text);

        // Card meshes are in card-local units (docked cards are scaled), so convert to meters.
        private static float Meters(CardState state) => Mathf.Max(.0001f, state.card.transform.lossyScale.x);

        private float CardDistance(CardState state, Vector3 point)
        {
            Vector3 local = state.card.transform.InverseTransformPoint(point);
            float dx = Mathf.Max(0f, Mathf.Abs(local.x) - state.paper.Width * .5f);
            float dy = Mathf.Max(0f, Mathf.Abs(local.y) - state.paper.Height * .5f);
            return new Vector3(dx, dy, local.z).magnitude * Meters(state);
        }

        private bool CardHit(CardState state, Ray ray, float margin, out float distance)
        {
            var t = state.card.transform;
            var plane = new Plane(t.forward, t.position);
            if (!plane.Raycast(ray, out distance)) return false;
            Vector3 local = t.InverseTransformPoint(ray.GetPoint(distance));
            float m = margin / Meters(state);
            return Mathf.Abs(local.x) <= state.paper.Width * .5f + m && Mathf.Abs(local.y) <= state.paper.Height * .5f + m;
        }

        private void UpdateHover(List<Vector3> tips, List<Pointing> rays, bool mouse, Ray mouseRay)
        {
            CardState best = null;
            float bestScore = float.PositiveInfinity;
            foreach (var state in cards)
            {
                if (!Hoverable(state)) continue;
                foreach (var tip in tips)
                {
                    float d = CardDistance(state, tip);
                    if (d < .12f && d < bestScore) { best = state; bestScore = d; }
                }
                foreach (var pointing in rays)
                    if (CardHit(state, pointing.ray, .015f, out float d) && .2f + d * .01f < bestScore) { best = state; bestScore = .2f + d * .01f; }
                if (mouse && CardHit(state, mouseRay, .01f, out float m) && .2f + m * .01f < bestScore) { best = state; bestScore = .2f + m * .01f; }
            }
            bool overTray = false;
            if (TrayVisible)
                for (int i = 0; i < trayButtons.Length && !overTray; i++)
                {
                    Vector3 p = trayButtons[i].position;
                    foreach (var tip in tips) if (Vector3.Distance(tip, p) < .05f) overTray = true;
                    foreach (var pointing in rays) if (SphereHit(pointing.ray, p, .025f, out _)) overTray = true;
                    if (mouse && SphereHit(mouseRay, p, .02f, out _)) overTray = true;
                }
            // No emoji while a card is being handled (held, stretched, crumpled).
            bool handling = false;
            foreach (var state in cards) handling |= state.card.IsHeld;
            if (handling) { best = null; overTray = false; trayCard = null; quietUntil = Time.time + .6f; }
            else if (Time.time < quietUntil) best = null;
            if (best != null && !(overTray && trayCard != null)) { trayCard = best; trayUntil = Time.time + .9f; }
            else if (overTray) trayUntil = Time.time + .9f;
            if (trayCard != null && (Time.time > trayUntil || !Hoverable(trayCard))) trayCard = null;
            if (tray.activeSelf != (trayCard != null)) tray.SetActive(trayCard != null);
        }

        // Editor/test entry point: show the sticker tray beside a card.
        public void ShowTray(TransferableSpeechBubble card)
        {
            EnsureSetup();
            trayCard = cards.Find(c => c.card == card);
            trayUntil = Time.time + 3f;
            if (tray != null) tray.SetActive(trayCard != null);
        }

        private void LayoutTray()
        {
            if (trayCard == null || !tray.activeSelf) return;
            var t = trayCard.card.transform;
            // A 2x2 sheet of emoji beside the card's right edge (offsets in meters), floating a
            // little toward the viewer so neighbouring cards do not hide it.
            Vector3 edge = t.TransformPoint(new Vector3(trayCard.paper.Width * .5f, 0f, 0f));
            for (int i = 0; i < trayButtons.Length; i++)
            {
                float x = .03f + (i % 2) * .046f, y = i < 2 ? .024f : -.024f;
                trayButtons[i].SetPositionAndRotation(edge + t.right * x + t.up * y - t.forward * .03f, t.rotation);
            }
        }

        public void Stick(TransferableSpeechBubble card, int kindIndex)
        {
            EnsureSetup();
            var state = cards.Find(c => c.card == card);
            if (state != null) Stick(state, kindIndex);
        }

        private void Stick(CardState state, int kindIndex)
        {
            if (state == null || kindIndex < 0 || kindIndex >= StickerKinds.Length) return;
            lastAction = Time.time;
            string kind = StickerKinds[kindIndex];
            Vector3 from = TrayVisible ? trayButtons[kindIndex].position : state.card.transform.position;
            var sticker = MakeSticker(kind, state.card.transform, material, font, 1f);
            sticker.position = from; // flies from the tray to the corner and shrinks (LayoutStuck)
            sticker.localScale = Vector3.one * (.001f / Meters(state)); // starts at tray size
            state.stuck.Add(sticker);
            state.stickers.Add(kind);
            string who = state.owner.characterName, text = Short(state.text);
            reactionLog.Add(kind + "  " + who + ": " + Clip(state.summary, 50));
            ToySounds.Play(ToySounds.Cue.Pop, from, .7f, 1.25f + kindIndex * .06f);
            ToyFx.Puff(from, .008f, 3);
            switch (kindIndex)
            {
                case 0: StartCoroutine(Cheer(new[] { state.owner }, 1.4f)); break;
                case 1: Ask("Wow, I love " + who + "'s idea: \"" + text + "\". Can someone build on it?"); break;
                case 2: Ask(who + ", I'm not sure I follow: \"" + text + "\". Can you explain it more simply?"); break;
                case 3: Ask("I'm not keen on " + who + "'s idea: \"" + text + "\". What else could we try?"); break;
            }
        }

        public int StickerCount(TransferableSpeechBubble card) => cards.Find(c => c.card == card)?.stickers.Count ?? 0;

        private void LayoutStuck()
        {
            foreach (var state in cards)
            {
                float w = state.paper.Width, h = state.paper.Height;
                // Stuck stickers step aside while the card is in the hand.
                bool showStuck = !state.card.IsHeld;
                foreach (var s in state.stuck) if (s != null && s.gameObject.activeSelf != showStuck) s.gameObject.SetActive(showStuck);
                if (!showStuck) continue;
                // Offsets and size in meters at the docked size; they grow with a reading card.
                float inv = 1f / Mathf.Max(.0001f, state.baseScale);
                for (int k = 0; k < state.stuck.Count; k++)
                {
                    var s = state.stuck[k];
                    if (s == null) continue;
                    var target = new Vector3(w * .5f - (.01f + k * .02f) * inv, h * .5f - .002f * inv, -.003f * inv);
                    float blend = 1f - Mathf.Exp(-Time.deltaTime * 14f);
                    s.localPosition = Vector3.Lerp(s.localPosition, target, blend);
                    s.localScale = Vector3.Lerp(s.localScale, Vector3.one * (.00062f * inv), blend);
                    s.localRotation = Quaternion.Euler(0f, 0f, k % 2 == 0 ? -12f : 10f);
                }
            }
        }

        // ---------------------------------------------------------------- taps

        private static bool SphereHit(Ray ray, Vector3 center, float radius, out float distance)
        {
            Vector3 to = center - ray.origin;
            distance = Vector3.Dot(to, ray.direction);
            return distance > 0f && (to - ray.direction * distance).sqrMagnitude <= radius * radius;
        }

        private IEnumerable<PaperSnapshot> TappableSnapshots(bool includeDecks)
        {
            if (includeDecks)
                foreach (var pair in decks)
                    for (int i = 0; i < pair.Value.Count; i++)
                        if (pair.Value[i].gameObject.activeSelf) yield return pair.Value[i];
            if (basketOpen) foreach (var s in discarded) yield return s;
        }

        private object RayTarget(Ray ray, bool includeCards, bool includeDecks = false)
        {
            object best = null;
            float bestDistance = 5f;
            void Consider(object target, bool hit, float d) { if (hit && d < bestDistance) { best = target; bestDistance = d; lastRayDistance = d; } }
            if (TrayVisible)
                for (int i = 0; i < trayButtons.Length; i++) { bool hit = SphereHit(ray, trayButtons[i].position, .022f, out float d); Consider("sticker:" + i, hit, d); }
            if (basket != null) { bool hit = SphereHit(ray, basket.position + Vector3.up * .025f, .055f, out float d); Consider("basket", hit, d); }
            if (notebook != null) { bool hit = SphereHit(ray, notebook.position, .045f, out float d); Consider("notebook", hit, d); }
            if (MinutesOpen)
            {
                var plane = new Plane(minutes.transform.forward, minutes.transform.position);
                if (plane.Raycast(ray, out float d))
                {
                    Vector3 local = minutes.transform.InverseTransformPoint(ray.GetPoint(d));
                    Consider("minutes", Mathf.Abs(local.x) < .17f && Mathf.Abs(local.y) < .135f, d);
                }
            }
            foreach (var snapshot in TappableSnapshots(includeDecks)) { bool hit = snapshot.Raycast(ray, out float d); Consider(snapshot, hit, d); }
            if (includeCards)
                foreach (var state in cards)
                    if (Hoverable(state) && CardHit(state, ray, .005f, out float d) && d > .3f) Consider(state, true, d);
            return best;
        }

        private void HandleTaps(List<Vector3> tips, List<bool> pinches, List<Pointing> rays, bool mouse, Ray mouseRay, bool mouseClick)
        {
            var touching = new HashSet<object>();
            for (int t = 0; t < tips.Count; t++)
            {
                Vector3 tip = tips[t];
                if (TrayVisible)
                    for (int i = 0; i < trayButtons.Length; i++) if (Vector3.Distance(tip, trayButtons[i].position) < .022f) touching.Add("sticker:" + i);
                if (basket != null && Vector3.Distance(tip, basket.position + Vector3.up * .025f) < .055f) touching.Add("basket");
                if (notebook != null && Vector3.Distance(tip, notebook.position) < .045f) touching.Add("notebook");
                if (MinutesOpen)
                {
                    Vector3 local = minutes.transform.InverseTransformPoint(tip);
                    if (Mathf.Abs(local.x) < .17f && Mathf.Abs(local.y) < .135f && Mathf.Abs(local.z) < .02f) touching.Add("minutes");
                }
                foreach (var snapshot in TappableSnapshots(false)) if (snapshot.Touches(tip)) touching.Add(snapshot);
                // Fingertip on the front of a card.
                foreach (var state in cards)
                {
                    if (!Hoverable(state)) continue;
                    Vector3 local = state.card.transform.InverseTransformPoint(tip);
                    float depth = local.z * Meters(state); // meters; the front faces -Z
                    if (depth > -.02f && depth < .01f && Mathf.Abs(local.x) < state.paper.Width * .5f && Mathf.Abs(local.y) < state.paper.Height * .5f)
                    {
                        touchedCards.Add(state);
                        if (!cardTouches.TryGetValue(state, out var touch)) cardTouches[state] = touch = new CardTouch { since = Time.time };
                        if (pinches[t]) touch.spoiled = true;
                    }
                }
            }
            foreach (var target in touching)
                if (!poked.Contains(target) && Time.time - lastAction > .35f) Activate(target);
            poked = touching;
            // Card taps complete when the finger lifts off: short, no pinch, no grab.
            endedTouches.Clear();
            foreach (var pair in cardTouches)
            {
                if (pair.Key.card.IsHeld) pair.Value.spoiled = true;
                if (!touchedCards.Contains(pair.Key)) endedTouches.Add(pair.Key);
            }
            foreach (var state in endedTouches)
            {
                var touch = cardTouches[state];
                cardTouches.Remove(state);
                if (!touch.spoiled && !state.card.IsHeld && Time.time - touch.since < .8f && Time.time - lastAction > .35f) Tap(state);
            }
            touchedCards.Clear();
            foreach (var pointing in rays)
                // Cards and deck copies: RemoteGrab (a quick pinch is a tap, a held one drags).
                if (pointing.press && Time.time - lastAction > .3f) { var target = RayTarget(pointing.ray, false); if (target != null) Activate(target); }
            // Cards are clicked through the Editor drag preview (click without drag = tap).
            if (mouse && mouseClick && Time.time - lastAction > .2f) { var target = RayTarget(mouseRay, false); if (target != null) Activate(target); }
        }

        private void Activate(object target)
        {
            lastAction = Time.time;
            switch (target)
            {
                case string key when key.StartsWith("sticker:"): Stick(trayCard, int.Parse(key.Substring(8))); break;
                case "basket": ToggleBasket(); break;
                case "notebook": ToggleMinutes(); break;
                case "minutes": ToggleMinutes(); break;
                case PaperSnapshot snapshot:
                    if (discarded.Contains(snapshot)) ToggleBasket();
                    else foreach (var pair in decks) if (pair.Value.Contains(snapshot)) { ToggleFan(pair.Key); break; }
                    break;
                case CardState state: Tap(state); break;
            }
        }

        // Editor drag preview: should a mouse press on this ray go to a sticker, deck, basket,
        // notebook or notes panel instead of starting a card drag?
        public bool OwnsRay(Ray ray) => Active && setup && RayTarget(ray, false) != null;
        private float lastRayDistance;
        // Stickers, basket, notebook or notes panel under the ray (for the far ring cursor).
        public bool UiHit(Ray ray, out float distance)
        {
            distance = 0f;
            if (!Active || !setup || RayTarget(ray, false) == null) return false;
            distance = lastRayDistance;
            return true;
        }

        public void EditorTap(TransferableSpeechBubble card)
        {
            if (!Active) return;
            EnsureSetup();
            var state = cards.Find(c => c.card == card);
            if (state != null && Hoverable(state)) Tap(state);
        }

        public void TapCard(TransferableSpeechBubble card)
        {
            EnsureSetup();
            var state = cards.Find(c => c.card == card);
            if (state != null) Tap(state);
        }

        // Tap = hear more from this character (or have it present its waiting line).
        private void Tap(CardState state)
        {
            lastAction = Time.time;
            ToySounds.Play(ToySounds.Cue.Press, state.card.transform.position, .6f, 1.1f);
            // A placed card: tap to expand it to the whole sentence, tap again to fold it.
            if (state.free) { SetExpanded(state, !state.expanded); return; }
            var actor = state.owner;
            // A question is open: tapping a card answers with it (Decisions = "not sure yet").
            if (Meeting.WaitingForAnswer && (actor.characterName == "Decisions" || actor.Pending.Count > 0)) { Meeting.Select(actor, true); return; }
            // A waiting line: have the character say it (or choose it while wrapping up).
            if (!Meeting.WaitingForAnswer && !actor.IsPresenting && actor.Pending.Count > 0) { Meeting.Select(actor, true); return; }
            Ask(actor.characterName + ", tell me more about \"" + Short(state.summary) + "\".");
        }

        private readonly HashSet<string> asked = new HashSet<string>();
        private void Ask(string line)
        {
            if (!Meeting.UsesArrivalSeats) return;
            asked.Add(line);
            Meeting.Submit(line);
        }

        private static string Short(string text)
        {
            text = (text ?? "").Replace('\n', ' ').Trim();
            return text.Length > 160 ? text.Substring(0, 157).TrimEnd() + "…" : text;
        }

        // ---------------------------------------------------------------- holding, reading, releasing

        private void UpdateHeld()
        {
            bool anyHeld = false;
            var head = Meeting.Head;
            foreach (var state in cards)
            {
                bool held = state.card.IsHeld;
                anyHeld |= held;
                if (held)
                {
                    var t = state.card.transform;
                    if (state.held && Time.deltaTime > 0f)
                        state.velocity = Vector3.Lerp(state.velocity, (t.position - state.heldAt) / Time.deltaTime, .5f);
                    else state.velocity = Vector3.zero;
                    state.heldAt = t.position;
                    state.heldRot = t.rotation;
                    state.heldWidth = state.paper.Width * t.lossyScale.x;
                    state.heldHeight = state.paper.Height * t.lossyScale.y;
                    state.heldShowsDetail = state.paper.IsOpen || state.reading;
                }
                // Brought up in front of the face (about forearm distance): unfold and enlarge.
                if (held && head != null)
                {
                    Vector3 offset = state.card.transform.position - head.position;
                    bool inFront = Vector3.Dot(head.forward, offset.normalized) > .25f;
                    if (!state.reading && offset.magnitude < ReadEnter && inFront) SetReading(state, true);
                    else if (state.reading && (offset.magnitude > ReadExit || !inFront)) SetReading(state, false);
                }
                else if (!held && state.held && state.reading && !state.editorReading) SetReading(state, false);
                state.held = held;
            }
            if (anyHeld == anyHeldBefore) return;
            anyHeldBefore = anyHeld;
            // Holding someone's thought: the team falls quiet and turns to the user.
            Meeting.UserHolding = anyHeld;
            foreach (var dock in arrival.handoff.docks)
            {
                var actor = dock.character;
                // Releasing only ends the listening pose; a cheer started by the drop stays.
                if (anyHeld ? actor.Cue != "thinking" : actor.Cue == "listen") actor.SetCue(anyHeld ? "listen" : "idle");
            }
        }

        public const float ReadEnter = .55f, ReadExit = .68f, ReadScale = 1.35f;
        public bool IsReading(TransferableSpeechBubble card) => cards.Find(c => c.card == card)?.reading ?? false;
        public float CardScale(TransferableSpeechBubble card) => cards.Find(c => c.card == card)?.scale ?? 1f;

        // Reading enlarges the card itself (the grab keeps the pinched spot under the fingers);
        // a card that comes home pops back in from small.
        private void UpdateCardScales()
        {
            float blend = 1f - Mathf.Exp(-Time.deltaTime * 10f);
            foreach (var state in cards)
            {
                float target = state.card.IsHeld ? (state.stretching ? state.stretchScale : 1f)
                    : state.reading ? ReadScale : state.expanded ? ExpandScale : 1f;
                if (Mathf.Abs(state.scale - target) < .001f && Mathf.Abs(state.card.transform.localScale.x - state.baseScale * target) < .0001f) continue;
                state.scale = Mathf.Abs(state.scale - target) < .002f ? target : Mathf.Lerp(state.scale, target, blend);
                state.card.transform.localScale = Vector3.one * (state.baseScale * state.scale);
            }
        }

        public void SetReading(TransferableSpeechBubble card, bool on)
        {
            EnsureSetup();
            var state = cards.Find(c => c.card == card);
            if (state == null) return;
            state.editorReading = on;
            SetReading(state, on);
        }

        private void SetReading(CardState state, bool on)
        {
            if (state.reading == on) return;
            state.reading = on;
            if (state.look != null) state.look.Reading = on || state.expanded;
            if (on || state.expanded) state.paper.OpenFully();
            else if (!state.free && state.owner.IsPresenting) state.paper.OpenFully();
            else state.paper.CloseToCompact();
            ToySounds.Play(ToySounds.Cue.Paper, state.card.transform.position, .5f, on ? 1.2f : .9f);
        }

        private void OnReleased(CardState state)
        {
            if (!Active) return;
            if (state.reading && !state.editorReading) SetReading(state, false);
            var card = state.card;
            if (state.paper.IsCrumpled) { ReleaseCrumpled(state); return; }
            // Judge by where the hand let go: the card may already have snapped to a nearby head.
            if (NearRing(state.heldAt)) { KeepAsKey(state, true); if (!state.free) ReturnHome(state); return; }
            if (card.Owner != null && card.Owner != state.home) { HandOff(state, card.Owner); if (!state.free) ReturnHome(state); return; }
            if (!state.free && card.Owner == state.home) return; // put back on its own head
            // Anywhere else it stays where it was let go (on top of a nearby card it joins that pile).
            if (state.free) { AddToPile(state, card.transform.position, card.transform.rotation); return; }
            if (string.IsNullOrWhiteSpace(state.text)) { ReturnHome(state); return; }
            LeaveFreeCard(state);
            RemoveLine(state);
            ReturnHome(state);
        }

        private void ReturnHome(CardState state)
        {
            if (state.card.Owner == state.home || state.card.IsHeld) return;
            state.home.Attach(state.card); // SpeechBubblePresentation pops it in at its owner
            ToyFx.Puff(state.card.transform.position, .008f, 3);
        }

        private bool NearRing(Vector3 point)
        {
            var ring = arrival.TopicRing;
            if (ring == null || !ring.enabled) return false;
            Vector3 offset = point - ring.transform.position;
            return new Vector2(offset.x, offset.z).magnitude < arrival.RingRadius + .09f && offset.y > -.05f && offset.y < .4f;
        }

        public void KeepCard(TransferableSpeechBubble card) { EnsureSetup(); var s = cards.Find(c => c.card == card); if (s != null) KeepAsKey(s, false); }
        public void DiscardCard(TransferableSpeechBubble card) { EnsureSetup(); var s = cards.Find(c => c.card == card); if (s != null) Discard(s); }
        public void HandCard(TransferableSpeechBubble card, SpeechBubbleDock receiver) { EnsureSetup(); var s = cards.Find(c => c.card == card); if (s != null && receiver != null) HandOff(s, receiver); }

        // Center ring: a key idea. Dropped by hand, it stays exactly where it was let go, at the
        // size it had; the line leaves the character's card (the idea now lives in the ring).
        // Everyone turns and cheers.
        private readonly HashSet<PaperSnapshot> placedIdeas = new HashSet<PaperSnapshot>();
        // In the ring every idea becomes a small chip with its short summary, standing where it
        // was dropped (kept inside the ring).
        public const float ChipWidth = .1f, ChipHeight = .05f;
        private void KeepAsKey(CardState state, bool dropped)
        {
            if (string.IsNullOrWhiteSpace(state.text)) return;
            lastAction = Time.time;
            string who = state.owner.characterName;
            var snapshot = PaperSnapshot.Create(transform, who, state.text, state.stickers, SpeechBubblePresentation.ResolveRoleColor(who),
                material, font, ChipWidth, ChipHeight);
            snapshot.ShowText(state.summary);
            snapshot.transform.SetPositionAndRotation(dropped ? state.heldAt : state.card.transform.position, state.card.transform.rotation);
            if (dropped)
            {
                Vector3 chipAt = ChipSpot(state.heldAt);
                snapshot.SetPose(chipAt, ChipRotationAt(chipAt), 1f, true);
                placedIdeas.Add(snapshot);
            }
            keyIdeas.Add(snapshot);
            arrival.SetRingIdeas(keyIdeas.Count);
            if (state.free) DestroyFree(state);
            else RemoveLine(state);
            var all = new List<WindupCharacter>();
            foreach (var dock in arrival.handoff.docks) all.Add(dock.character);
            StartCoroutine(Cheer(all.ToArray(), 1.8f));
            Vector3 at = arrival.TopicRing != null ? arrival.TopicRing.transform.position : state.card.transform.position;
            ToySounds.Play(ToySounds.Cue.Chime, at, .7f, 1.1f);
            ToyFx.Confetti(at + Vector3.up * .1f, 12);
        }

        private Vector3 ChipSpot(Vector3 drop)
        {
            Vector3 c = arrival.TopicRing.transform.position, flat = Vector3.ProjectOnPlane(drop - c, Vector3.up);
            float max = Mathf.Max(.02f, arrival.RingRadius - .035f);
            if (flat.magnitude > max) flat = flat.normalized * max;
            return c + flat + Vector3.up * (ChipHeight * .5f + .012f);
        }

        private static Quaternion ChipRotationAt(Vector3 position) => SpeechBubbleDock.FacingAt(position) * Quaternion.Euler(10f, 0f, 0f);

        // Another character's head: they think about it from their perspective, then speak.
        private void HandOff(CardState state, SpeechBubbleDock receiver)
        {
            if (string.IsNullOrWhiteSpace(state.text) || receiver.character == state.owner) return;
            lastAction = Time.time;
            PushHistory(receiver.character, state.owner.characterName, state.text, state.stickers, state.line);
            WindupBody.Of(receiver.character)?.Play("Receive");
            ToySounds.Play(ToySounds.Cue.Paper, receiver.anchor.position, .7f, 1.1f);
            ToyFx.Puff(receiver.anchor.position, .01f, 3);
            Meeting.RequestHandoffReview(receiver.character, state.line);
            // The card now belongs to the receiver (its deck); it leaves where it came from.
            if (state.free) DestroyFree(state);
            else RemoveLine(state);
        }

        // Crumpled: the idea flies into Benefit's basket. Benefit points out what was good.
        private void Discard(CardState state) => Discard(state, false);

        private void Discard(CardState state, bool fly)
        {
            lastAction = Time.time;
            string text = state.text, who = state.owner.characterName;
            if (!string.IsNullOrWhiteSpace(text))
            {
                var ball = PaperSnapshot.Create(transform, who, text, state.stickers, SpeechBubblePresentation.ResolveRoleColor(who), material, font);
                ball.transform.position = state.card.transform.position;
                ball.Line = state.line;
                ball.SetBall(true);
                discarded.Add(ball);
                if (fly && basket != null) StartCoroutine(FlyIntoBasket(ball, state.card.transform.position));
            }
            if (state.free) DestroyFree(state);
            else
            {
                state.paper.OpenFully();
                state.paper.CloseToCompact();
                RemoveLine(state); // the discarded line goes to the basket, not the deck
                ReturnHome(state);
            }
            Vector3 at = basket != null ? basket.position : state.card.transform.position;
            ToySounds.Play(ToySounds.Cue.Paper, at, .8f, .85f);
            ToyFx.Puff(at + Vector3.up * .04f, .012f, 4);
            // Benefit's job: find what is still worth keeping and offer it back.
            if (!string.IsNullOrWhiteSpace(text))
                Ask("Benefit, I'm throwing away " + who + "'s idea: \"" + Short(text) + "\". Pull out the one part that is still worth keeping, " +
                    "say it in a short line, and ask if I want to keep it.");
        }

        // The current line leaves the character's card (kept in the ring or thrown away).
        private void RemoveLine(CardState state)
        {
            if (state.free) return;
            ClearStickers(state);
            var actor = state.owner;
            if (actor.IsPresenting) actor.Finish();
            else if (actor.Pending.Count > 0) actor.Pending.Dequeue();
            actor.Refresh();
            SyncLine(state);
        }

        private IEnumerator Cheer(WindupCharacter[] actors, float seconds)
        {
            foreach (var actor in actors)
            {
                WindupBody.Of(actor)?.Play("Cheer");
                if (actor.Cue != "thinking") actor.SetCue("encourage");
            }
            yield return new WaitForSeconds(seconds);
            foreach (var actor in actors) if (actor.Cue == "encourage") actor.SetCue("idle");
        }

        private void LayoutKept()
        {
            var ring = arrival.TopicRing;
            if (ring == null) return;
            Vector3 c = ring.transform.position;
            Vector3 toward = TowardUser(c), side = Vector3.Cross(Vector3.up, toward);
            // Ideas dropped by hand stay where they were put; only keyboard-kept ones are arranged.
            var arranged = keyIdeas.FindAll(idea => !placedIdeas.Contains(idea));
            int n = arranged.Count;
            for (int i = 0; i < n; i++)
            {
                int row = i / 5, inRow = Mathf.Min(5, n - row * 5), column = i % 5;
                float angle = inRow == 1 ? 0f : Mathf.Lerp(-65f, 65f, column / (inRow - 1f));
                Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * -toward;
                Vector3 position = c + dir * (arrival.RingRadius * .62f) + Vector3.up * (ChipHeight * .5f + .012f + row * .055f);
                arranged[i].SetPose(position, ChipRotationAt(position), 1f, true);
            }
        }

        // ---------------------------------------------------------------- basket and minutes

        public void ToggleBasket()
        {
            EnsureSetup();
            basketOpen = !basketOpen && discarded.Count > 0;
            basketShakeAt = Time.time;
            lastAction = Time.time;
            // Benefit shakes the basket out: the ideas pop out between frames 14 and 34.
            basketRevealAt = Time.time;
            if (basketOpen && benefitBody != null)
            {
                benefitBody.Play("Benefit_BasketShake");
                basketRevealAt = Time.time + BasketShakeFrom / 30f;
            }
            if (basket != null)
            {
                ToySounds.Play(ToySounds.Cue.Paper, basket.position, .8f, basketOpen ? 1.25f : .85f);
                ToyFx.Puff(basket.position + Vector3.up * .05f, .01f, 4);
            }
        }

        private void LayoutBasket()
        {
            if (basket == null) return;
            PlaceBasket();
            PlaceNotebook();
            float since = Time.time - basketShakeAt;
            if (benefitBody != null) basket.rotation = Quaternion.identity;
            else basket.rotation = since < .7f ? Quaternion.Euler(Mathf.Sin(since * 40f) * 14f * (1f - since / .7f), 0f, Mathf.Cos(since * 34f) * 10f * (1f - since / .7f)) : Quaternion.identity;
            var facing = SpeechBubbleDock.FacingAt(basket.position + Vector3.up * .2f);
            Vector3 right = facing * Vector3.right, back = facing * Vector3.forward;
            int n = discarded.Count, shown = Mathf.Min(5, n);
            for (int i = 0; i < n; i++)
            {
                var item = discarded[n - 1 - i];
                if (flyingBalls.Contains(item)) continue;
                float revealAt = basketRevealAt + (shown > 1 ? i * (BasketShakeTo - BasketShakeFrom) / 30f / (shown - 1) : 0f);
                if (basketOpen && i < shown && Time.time >= revealAt)
                {
                    item.SetBall(false);
                    float offset = i - (shown - 1) * .5f;
                    item.SetPose(basket.position + Vector3.up * .2f - back * .06f + right * (offset * .15f), facing, .9f, true);
                }
                else
                {
                    item.SetBall(true);
                    float a = i * 2.4f;
                    item.SetPose(basket.position + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * .014f + Vector3.up * (.028f + .01f * (i / 3)),
                        Quaternion.Euler(i * 37f, i * 53f, 0f), 1f, false);
                }
            }
        }

        public void ToggleMinutes()
        {
            EnsureSetup();
            lastAction = Time.time;
            bool open = !minutes.activeSelf;
            minutes.SetActive(open);
            if (open) WindupBody.Of(Actor("Decisions"))?.Play("Decision_Present");
            var head = Meeting.Head;
            if (open && head != null)
            {
                // Where the user is looking, pitch limited so it is never on the floor or ceiling.
                Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                if (flat.sqrMagnitude < .001f) flat = Vector3.forward;
                float pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(head.forward.y, -1f, 1f)) * Mathf.Rad2Deg, -45f, 5f) * Mathf.Deg2Rad;
                Vector3 forward = flat * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch);
                Vector3 position = head.position + forward * .42f; // nearer than the cards on the table
                minutes.transform.SetPositionAndRotation(position, Quaternion.LookRotation(position - head.position, Vector3.up));
                minutesText.text = MinutesBody();
                var decisions = Actor("Decisions");
                if (decisions != null) StartCoroutine(Cheer(new[] { decisions }, 1.2f));
            }
            ToySounds.Play(ToySounds.Cue.Paper, notebook != null ? notebook.position : transform.position, .7f, open ? 1.15f : .9f);
        }

        // Decisions' notes: short, scannable sections. The prompts the gestures sent on the
        // user's behalf are left out of "Recent"; their effect is listed in its own section.
        private string MinutesBody()
        {
            var text = new StringBuilder();
            string topic = arrival.ConfirmedTopic;
            text.Append("<b>Topic</b>\n").Append(string.IsNullOrWhiteSpace(topic) ? "—" : Clip(topic, 110)).Append("\n\n");
            Section(text, "Key ideas", keyIdeas.ConvertAll(idea => idea.Speaker + ": " + Clip(idea.Text, 70)), 4);
            Section(text, "Reactions", reactionLog, 3);
            Section(text, "Set aside", discarded.ConvertAll(idea => idea.Speaker + ": " + Clip(idea.Text, 70)), 3);
            var recent = new List<string>();
            foreach (var line in Meeting.History)
            {
                if (line.speaker == "User" && asked.Contains(line.body)) continue;
                recent.Add((line.speaker == "User" ? "Me" : line.speaker) + ": " +
                           Clip(string.IsNullOrWhiteSpace(line.summary) ? line.body : line.summary, 60));
            }
            Section(text, "Recent", recent, 4);
            if (!string.IsNullOrWhiteSpace(Meeting.SelectedDirection)) text.Append("<b>Next step</b>\n").Append(Clip(Meeting.SelectedDirection, 110));
            return text.ToString().TrimEnd();
        }

        private static void Section(StringBuilder text, string title, List<string> items, int keep)
        {
            text.Append("<b>").Append(title).Append("</b>\n");
            if (items.Count == 0) text.Append("—\n");
            int from = Mathf.Max(0, items.Count - keep);
            if (from > 0) text.Append("<color=#8A8A93>+").Append(from).Append(" earlier</color>\n");
            for (int i = from; i < items.Count; i++) text.Append("• ").Append(items[i]).Append('\n');
            text.Append('\n');
        }

        private static string Clip(string value, int length)
        {
            value = (value ?? "").Replace('\n', ' ').Trim();
            return value.Length > length ? value.Substring(0, length - 1).TrimEnd() + "…" : value;
        }

#if UNITY_EDITOR
        // Editor stand-ins for gestures that need real hands: hover a card with the mouse, then
        // F = pull close (read), E = expand, R = keep in ring, X = crumple into the basket,
        // H = hand to the next character, 1-4 = stickers, left/right arrows = swipe.
        private void EditorKeys()
        {
            if (UnityEngine.XR.XRSettings.isDeviceActive) return;
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;
            // Arrow keys stand in for thumb microgesture swipes (hovered pile/deck, else gaze).
            if (keyboard.leftArrowKey.wasPressedThisFrame) Swipe(1);
            if (keyboard.rightArrowKey.wasPressedThisFrame) Swipe(-1);
            if (trayCard == null) return;
            var state = trayCard;
            // E stands in for the two-hand pull-apart (expand); C for a fist (crumple).
            if (keyboard.eKey.wasPressedThisFrame) SetExpanded(state, !state.expanded);
            if (keyboard.fKey.wasPressedThisFrame) { state.editorReading = !state.reading; SetReading(state, !state.reading); }
            if (keyboard.rKey.wasPressedThisFrame) KeepAsKey(state, false);
            if (keyboard.xKey.wasPressedThisFrame) Discard(state);
            if (keyboard.hKey.wasPressedThisFrame)
            {
                var docks = arrival.handoff.docks;
                int index = Array.IndexOf(docks, state.home ?? DockOf(state.owner));
                HandOff(state, docks[(index + 1) % docks.Length]);
            }
            if (keyboard.digit1Key.wasPressedThisFrame) Stick(state, 0);
            if (keyboard.digit2Key.wasPressedThisFrame) Stick(state, 1);
            if (keyboard.digit3Key.wasPressedThisFrame) Stick(state, 2);
            if (keyboard.digit4Key.wasPressedThisFrame) Stick(state, 3);
        }
#endif
    }
}
