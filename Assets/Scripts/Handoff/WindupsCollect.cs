using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    // F78 airplane mode (2026-11-14, the user: with no internet the crew cannot talk or make anything, so just collect:
    // post-its, photos, sketches; "a keyboard instead of the mic: peel a post-it and the keyboard comes up in front of
    // me"; "all of this only in airplane mode"; no room places: "put them anywhere, and when I come back show me
    // everything I collected in front of me"; after the first Quest try: "more in front of me, and do all the steps").
    // Collecting (Begin): the crew stands on the title sign (WindupsTitle.PlateMode, no room scan) and each holds out
    // its tool: Fixer a blank post-it (taken: the Quest system keyboard, WindupsKeyboard; the note comes in front of the
    // eyes and the words appear on it), Maker its sketchbook (MakerSession.Collect.cs: the page opens where it is let go,
    // the check keeps the drawing), Spark its frame (carried while pinched, its red button takes the picture). Every
    // finished thing goes to the first free place of a row in front of the eyes (CollectSlot) and stays wherever it is
    // put afterwards: picked up near by a pinch or from afar by an aimed pinch (it flies to the hand, grab & release),
    // crumpled by a fist or the other hand's pinch while carried (the ball flies to Fixer and the thing is gone).
    // Saved by content only (collected.json + collected/*.jpg, no room anchors); on the next start the unused ones fly
    // out of Spark into an arc in front of the person (offline: then collecting goes on; online: ShowOnline, a post-it
    // let go on the topic note or the current note is that idea, a picture or sketch there is a reference).
    public sealed class WindupsCollect : MonoBehaviour
    {
        [Serializable]
        public sealed class Item
        {
            public string kind = "note", text, at, variant, image;
            public bool used;
            // F91: pinned to a real wall ("wall", against pinAnchor) or to the idea wall's board ("board"); null: not pinned.
            public string pinOn, pinAnchor;
            public float[] pinLocal, pinTurn, pinWorld, pinWorldTurn;
        }
        [Serializable] private sealed class Data { public List<Item> items = new List<Item>(); }

        public enum Step { Off, Offering, Typing, Placing, Showing, Editing }

        public const float SettleSeconds = 2.5f, SheetSize = .085f, PictureSize = .11f, GrabReach = .04f, FarAimDwell = .3f;
        public const float ArcRadius = .45f, ArcBelowEyes = .04f, ArcSpacing = .125f, ArcRowGap = .13f;   // the arc on coming back
        public const int ArcPerRow = 6, ArcRows = 2;
        public const float TypingAhead = .34f, TypingBelowEyes = -.02f; // where the note is written: a little above the eyes (over the system keyboard and the crew's cards)
        public const float CollectAhead = .36f;                         // the row new things land in
        public const string Placeholder = "Write an idea";
        public const float RewriteRoom = 27f; // mm at the bottom of a kept post-it left to its Rewrite button

        public static string SavePath => Path.Combine(Application.persistentDataPath, "collected.json");
        public static string ImageDir => Path.Combine(Application.persistentDataPath, "collected");
        public static bool HasItems => Load().items.Any(i => !i.used);
        public static void Forget()
        {
            try { if (File.Exists(SavePath)) File.Delete(SavePath); } catch { }
            try { if (Directory.Exists(ImageDir)) Directory.Delete(ImageDir, true); } catch { }
        }

        public Step Current { get; private set; }
        public bool Online { get; private set; }
        public int Collected => data?.items.Count ?? 0;
        public IReadOnlyList<Item> Items => data?.items;
        public IReadOnlyList<Transform> Views => views;
        public WindupsKeyboard Keyboard => keyboard;
        public int Shown { get; private set; }      // things laid out in the arc at the start
        public int Crumpled { get; private set; }
        public int Used { get; private set; }
        public int FarTakes { get; private set; }
        public bool Carrying => carry != null;
        public Transform CarriedView => carry?.view;
        // F91 (MakerSession.CollectPins.cs): pins on walls.
        public int PinsPut { get; private set; }
        public int PinsRestored { get; private set; }
        public bool IsPinned(Transform view) => ItemOf(view)?.pinOn != null;
        public bool PinShown(Transform view) => view != null && pinOf.TryGetValue(view, out var pin) && pin != null && pin.gameObject.activeSelf;
        public Transform PinOf(Transform view) => view != null && pinOf.TryGetValue(view, out var pin) ? pin : null;
        public const float PinOut = .03f, PinIn = .0026f; // the pin hovering over the card while carried by a wall, and pushed in
        public Item ItemOf(Transform view) => view != null && itemOf.TryGetValue(view, out var item) ? item : null;

        private WindupsArrival arrival;
        private MakerSession session;
        private TransferableSpeechBubble note;
        private WindupCharacter fixer, spark, maker;
        private Data data;
        private WindupsKeyboard keyboard;
        private string writtenText;
        private float quietFor, frameAgainAt = -1f, bookAgainAt = -1f;
        private bool flown;
        private readonly List<Transform> views = new List<Transform>();
        private readonly Dictionary<Transform, Item> itemOf = new Dictionary<Transform, Item>();
        private readonly Dictionary<Transform, Vector2> sizeOf = new Dictionary<Transform, Vector2>();
        private readonly Dictionary<Transform, List<UnityEngine.Object>> ownedOf = new Dictionary<Transform, List<UnityEngine.Object>>();
        private readonly Dictionary<Transform, Text> wordsOf = new Dictionary<Transform, Text>();
        private readonly Dictionary<Transform, Transform> pinOf = new Dictionary<Transform, Transform>();
        private readonly Dictionary<Transform, float> pinShownAt = new Dictionary<Transform, float>();
        private readonly Dictionary<Transform, Coroutine> pinFlights = new Dictionary<Transform, Coroutine>();
        // Rewriting a kept post-it (its Rewrite button): the post-it, the step to go back to, where it was.
        private Transform editing;
        private Step editBefore;
        private Pose editHome;
        private Coroutine editFlight;
        public Transform Editing => editing;
        public int Rewrites { get; private set; }
        private readonly Dictionary<Hand, bool> wasPinching = new Dictionary<Hand, bool>();
        private readonly Dictionary<Hand, (Transform view, float since)> aimed = new Dictionary<Hand, (Transform, float)>();
        private static readonly object saveLock = new object();

        private sealed class Carry { public Transform view; public Hand hand; public Vector3 offset; public float distance, fistSince = -1f; public bool mouse; }
        public const float FistHold = .25f; // a fist held this long while carrying crumples (a pinch with curled fingers is not one)
        private Carry carry;

        private Transform Head => arrival != null ? arrival.handoff.meeting.Head : null;

        // ------------------------------------------------------------ start

        // Offline. `introduced`: the introduction just said what this is (no line of its own then).
        public void Begin(WindupsArrival arrival, MakerSession session, bool introduced)
        {
            Setup(arrival, session);
            session.StartCollecting();
            session.PictureCollected = (jpeg, texture, at, turn) => AddPicture("photo", jpeg, texture, at);
            session.DrawingCollected = (jpeg, texture, at, turn) => AddPicture("sketch", jpeg, texture, at);
            session.CollectHandBusy = hand => (carry != null && carry.hand == hand) || NoteHand(hand);
            session.CollectedOnRay = ray => RayHit(ray, out float d) != null ? d : -1f;
            var earlier = Earlier();
            if (earlier.Count > 0)
            {
                session.OfflineLine(Crew.Spark, "Look what you collected!");
                StartCoroutine(ShowCollected(earlier));
            }
            else if (!introduced) session.OfflineLine(Crew.Maker, "No internet today. Let's collect ideas!");
            float after = earlier.Count > 0 ? 1.6f + .09f * Mathf.Min(earlier.Count, ArcPerRow * ArcRows) : .8f;
            StartCoroutine(OfferAfter(after));
            Debug.Log("[Windups Collect] airplane mode: " + earlier.Count + " earlier thing(s)");
        }

        // Online, coming back with things collected: they are shown in the arc to be used (no tools are held out).
        public void ShowOnline(WindupsArrival arrival, MakerSession session)
        {
            Setup(arrival, session);
            Online = true;
            Current = Step.Showing;
            session.CollectedOnRay = ray => RayHit(ray, out float d) != null ? d : -1f;
            session.CollectHandBusy = hand => carry != null && carry.hand == hand; // F91: the session leaves a carrying hand alone
            var earlier = Earlier();
            if (earlier.Count == 0) return;
            // F91: what was pinned to a wall goes back up there (not into the arc).
            earlier = earlier.Where(item => !RestorePinned(item)).ToList();
            if (earlier.Count == 0) return;
            session.OfflineLine(Crew.Spark, "Look what you collected!");
            StartCoroutine(ShowCollected(earlier));
            Debug.Log("[Windups Collect] back online: " + earlier.Count + " collected thing(s) shown");
        }

        private void Setup(WindupsArrival arrival, MakerSession session)
        {
            this.arrival = arrival;
            this.session = session;
            note = arrival.TopicNote;
            session.PrepareOffline();
            fixer = session.CrewCharacter(Crew.Fixer);
            spark = session.CrewCharacter(Crew.Spark);
            maker = session.CrewCharacter(Crew.Maker);
            data = Load();
        }

        private List<Item> Earlier() => data.items.Where(i => !i.used && (i.kind == "note" ? !string.IsNullOrWhiteSpace(i.text) : File.Exists(ImagePath(i)))).ToList();

        private IEnumerator OfferAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            OfferNote();
            yield return new WaitForSeconds(.35f);
            session.OfferCollectBook();
            yield return new WaitForSeconds(.35f);
            PutFrameOut();
        }

        private void OfferNote()
        {
            if (fixer == null) return;
            arrival.OfferNoteFrom(fixer);
            Current = Step.Offering;
        }

        // Spark's frame stands beside it on the sign, on its outer side, facing the eyes.
        private void PutFrameOut()
        {
            if (spark == null || session.CollectFrameOut) return;
            Vector3 at = spark.transform.position;
            Vector3 middle = maker != null ? maker.transform.position : at + Vector3.right * .14f;
            Vector3 outward = Vector3.ProjectOnPlane(at - middle, Vector3.up);
            if (outward.sqrMagnitude < 1e-4f && Head != null) outward = -Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(Head.forward, Vector3.up));
            outward = outward.normalized;
            session.PutCollectFrameOut(at + outward * .15f + Vector3.up * .09f);
        }

        // ------------------------------------------------------------ each frame

        private void Update()
        {
            if (arrival == null) return;
            if (!Online) session.CollectingFrame();
            var rewrite = session.CollectButtonsFrame(hand => carry != null && carry.hand == hand);
            if (rewrite != null && MayRewrite) StartRewrite(rewrite);
            switch (Current)
            {
                case Step.Offering:
                    if (note.IsHeld) Take();
#if UNITY_EDITOR
                    else if (MouseDown(out var ray) && note.Box != null && note.Box.Raycast(ray, out _, 8f)) Take();
#endif
                    break;
                case Step.Typing:
                    // Let go (on the Quest the keyboard takes the hands away at once), the note comes in front of the eyes.
                    if (!flown && !note.IsHeld) { flown = true; StartCoroutine(FlyNote(TypingSpot())); }
                    keyboard.Poll();
                    if (keyboard.Text != writtenText) { writtenText = keyboard.Text; note.SetText("Me", writtenText, writtenText); }
                    if (keyboard.Status == WindupsKeyboard.State.Done) Written();
                    else if (keyboard.Status == WindupsKeyboard.State.Cancelled) OfferNote();
                    break;
                case Step.Placing:
                    quietFor = note.IsHeld ? 0f : quietFor + Time.deltaTime;
                    if (quietFor >= SettleSeconds) Keep();
                    break;
                case Step.Editing:
                    keyboard.Poll();
                    if (keyboard.Text != writtenText) { writtenText = keyboard.Text; SetWords(editing, writtenText); }
                    if (keyboard.Status == WindupsKeyboard.State.Done) EndRewrite(true);
                    else if (keyboard.Status == WindupsKeyboard.State.Cancelled) EndRewrite(false);
                    break;
            }
            if (!Online) KeepToolsOut();
            CarryFrame();
        }

        private void LateUpdate()
        {
            if (arrival != null && !Online) session.CollectingLate();
        }

        // The frame and the book come back out a moment after they were put away (a picture or a drawing kept).
        private void KeepToolsOut()
        {
            if (Current == Step.Off) return;
            if (!session.CollectFrameOut)
            {
                if (frameAgainAt < 0f) frameAgainAt = Time.time + 1.2f;
                else if (Time.time > frameAgainAt) { frameAgainAt = -1f; PutFrameOut(); }
            }
            if (!session.CollectBookOut)
            {
                if (bookAgainAt < 0f) bookAgainAt = Time.time + 1.4f;
                else if (Time.time > bookAgainAt) { bookAgainAt = -1f; session.OfferCollectBook(); }
            }
        }

        // ------------------------------------------------------------ the post-it

        private bool NoteHand(Hand hand) => false; // (the note's own SDK grab keeps its hand; nothing to add)

        private void Take()
        {
            arrival.TakeCollectNote();
            flown = false;
            writtenText = "";
            keyboard = WindupsKeyboard.Open(Placeholder);
            Current = Step.Typing;
            if (fixer != null) fixer.SetCue("encourage");
        }

        private void Written()
        {
            string text = (keyboard.Text ?? "").Trim();
            if (text.Length == 0) { OfferNote(); return; }
            note.SetText("Me", text, text);
            writtenText = text;
            quietFor = 0f;
            Current = Step.Placing;
            if (fixer != null) fixer.SetCue("idle");
            session.OfflineReact(Crew.Fixer, "Got it!");
        }

        // The written note becomes a collected one where it is, and Fixer holds out the next.
        private void Keep()
        {
            var item = new Item { kind = "note", text = writtenText, at = DateTime.Now.ToString("s"), variant = WindupsPostIt.Variants[data.items.Count % 3] };
            data.items.Add(item);
            Save(null, null);
            var t = note.transform;
            var view = NoteView(item, t.position, Facing(t.position));
            float width = note.Box != null ? note.Box.size.x * t.lossyScale.x : SheetSize;
            StartCoroutine(FoldIn(view, Mathf.Clamp(width / SheetSize, 1f, 4f)));
            ToySounds.Play(ToySounds.Cue.Paper, t.position, .7f, 1.2f);
            ToyFx.Puff(t.position, .01f, 4);
            Debug.Log("[Windups Collect] kept note " + data.items.Count + ": " + item.text);
            OfferNote();
        }

        // ------------------------------------------------------------ rewriting a kept post-it

        // (2026-11-14, Quest: "there is no button for when I want to fix a post-it") A kept post-it's Rewrite button
        // (MakerSession.AddCollectRewrite) brings it in front of the eyes with the keyboard open on its words; Done keeps
        // the new words (saved) and it flies back to where it was; Cancel, or Done with nothing left, keeps the old ones
        // (a fist or the other hand's pinch is the way to throw it away). Not while a note is being written or carried.
        private bool MayRewrite => editing == null && carry == null && (Current == Step.Offering || Current == Step.Showing) && !(note != null && note.IsHeld);

        private void StartRewrite(Transform view)
        {
            var item = ItemOf(view);
            if (item == null || item.kind != "note" || !MayRewrite) return;
            editing = view;
            editBefore = Current;
            editHome = new Pose(view.position, view.rotation);
            writtenText = item.text ?? "";
            session.ShowCollectRewrite(view, false);
            keyboard = WindupsKeyboard.Open(Placeholder, writtenText);
            Current = Step.Editing;
            Rewrites++;
            Vector3 to = TypingSpot();
            editFlight = StartCoroutine(FlyTo(view, to, Facing(to), .3f));
            ToySounds.Play(ToySounds.Cue.Paper, view.position, .6f, 1.25f);
            if (fixer != null) fixer.SetCue("encourage");
            Debug.Log("[Windups Collect] rewriting: " + item.text);
        }

        private void EndRewrite(bool keep)
        {
            var view = editing;
            var item = ItemOf(view);
            editing = null;
            Current = editBefore;
            if (fixer != null) fixer.SetCue("idle");
            if (view == null || item == null) return;
            string text = (keyboard?.Text ?? "").Trim();
            bool changed = keep && text.Length > 0 && text != item.text;
            if (changed)
            {
                item.text = text;
                Save(null, null);
                session.OfflineReact(Crew.Fixer, "Got it!");
            }
            SetWords(view, item.text);
            session.ShowCollectRewrite(view, true);
            if (editFlight != null) StopCoroutine(editFlight);
            editFlight = StartCoroutine(FlyTo(view, editHome.position, editHome.rotation, .3f));
            Debug.Log("[Windups Collect] rewrite " + (changed ? "kept: " + item.text : "left as it was"));
        }

        private void SetWords(Transform view, string text)
        {
            if (view != null && wordsOf.TryGetValue(view, out var words) && words != null) words.text = text ?? "";
        }

        // Where the note is written: TypingAhead in front of the eyes, on the first place of the row no collected thing
        // takes (so a new note never lands on an earlier one).
        public Vector3 TypingSpot() => FreeSlot(TypingAhead, TypingBelowEyes);

        // The first free place of a row `ahead` in front of the eyes `below` them (the middle, then right, left, ...;
        // a second row above).
        public Vector3 FreeSlot(float ahead, float below)
        {
            var head = Head;
            if (head == null) return note.transform.position;
            Vector3 forward = Flat(head.forward);
            Vector3 first = Vector3.zero;
            for (int i = 0; i < ArcPerRow * ArcRows; i++)
            {
                Vector3 spot = RowSlot(i, head.position, forward, ahead, below);
                if (i == 0) first = spot;
                if (views.All(v => v == null || v == editing || (carry != null && carry.view == v) || Vector3.Distance(v.position, spot) > SheetSize)) return spot;
            }
            return first;
        }

        public static Vector3 RowSlot(int i, Vector3 eyes, Vector3 forward, float ahead, float below)
        {
            int row = i / ArcPerRow, k = i % ArcPerRow;
            int side = k == 0 ? 0 : (k % 2 == 1 ? (k + 1) / 2 : -(k / 2));
            float angle = side * (PictureSize + .02f) / ahead * Mathf.Rad2Deg;
            Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * forward;
            return eyes + dir * ahead + Vector3.down * below + Vector3.up * row * (PictureSize + .03f);
        }

        // Kept for the checks (the first version's name).
        public static Vector3 TypingSlot(int i, Vector3 eyes, Vector3 forward) => RowSlot(i, eyes, forward, TypingAhead, TypingBelowEyes);

        private IEnumerator FlyNote(Vector3 to)
        {
            var t = note.transform;
            Vector3 from = t.position;
            Quaternion fromTurn = t.rotation;
            for (float k = 0f; k < 1f; k += Time.deltaTime / .35f)
            {
                if (note.IsHeld || Current != Step.Typing && Current != Step.Placing) yield break;
                float e = Mathf.SmoothStep(0f, 1f, k);
                t.position = Vector3.Lerp(from, to, e) + Vector3.up * Mathf.Sin(k * Mathf.PI) * .03f;
                t.rotation = Quaternion.Slerp(fromTurn, Facing(to), e);
                yield return null;
            }
            if (note.IsHeld) yield break;
            t.SetPositionAndRotation(to, Facing(to));
        }

        // ------------------------------------------------------------ photos and sketches

        // A picture from Spark's frame or a drawing from Maker's sketchbook: kept, and it flies from where it came out
        // to the first free place in front of the eyes.
        private void AddPicture(string kind, byte[] jpeg, Texture2D texture, Vector3 from)
        {
            if (jpeg == null || texture == null) return;
            string name = kind + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".jpg";
            var item = new Item { kind = kind, at = DateTime.Now.ToString("s"), image = name };
            data.items.Add(item);
            Save(name, jpeg);
            Vector3 to = FreeSlot(CollectAhead, TypingBelowEyes);
            var view = PictureView(item, texture, from, Facing(from));
            StartCoroutine(FlyTo(view, to, Facing(to), .3f));
            if (kind == "photo") session.OfflineReact(Crew.Spark, "Got it!");
            else session.OfflineReact(Crew.Maker, "Nice!");
            Debug.Log("[Windups Collect] kept " + kind + " " + data.items.Count + ": " + name + " (" + texture.width + "x" + texture.height + ")");
        }

        // ------------------------------------------------------------ the things (views)

        private Quaternion Facing(Vector3 at)
        {
            var head = Head;
            Vector3 away = head != null ? at - head.position : Vector3.forward;
            if (away.sqrMagnitude < 1e-6f) away = Vector3.forward;
            return Quaternion.LookRotation(away.normalized, Vector3.up);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v = Vector3.ProjectOnPlane(v, Vector3.up);
            return v.sqrMagnitude < 1e-4f ? Vector3.forward : v.normalized;
        }

        // A post-it with the words on it (read from -Z, like the wall notes): the post-it model and a legacy Text drawn
        // crisp by ToyTextMirror (the legacy Text draws what the TMP font cannot, e.g. Korean without a system font).
        private Transform NoteView(Item item, Vector3 position, Quaternion rotation)
        {
            var root = NewView("Collected Note", item, position, rotation, new Vector2(SheetSize, SheetSize));
            var model = Resources.Load<GameObject>("WindupsPostIt_" + (string.IsNullOrEmpty(item.variant) ? "A" : item.variant));
            if (model != null)
            {
                var look = Instantiate(model, root, false).transform;
                look.name = "Sheet";
                look.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                float s = SheetSize / WindupsPostIt.ModelSize;
                look.localScale = new Vector3(s, Mathf.Min(2f, s), s);
            }
            var rect = Canvas(root, SheetSize, SheetSize, -.004f);
            var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var textRect = (RectTransform)textObject.transform;
            textRect.SetParent(rect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, RewriteRoom); // (the Rewrite button keeps the lower left)
            textRect.offsetMax = new Vector2(-8f, -8f);
            var text = textObject.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = item.text;
            text.color = new Color(.2f, .19f, .22f);
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 6;
            text.resizeTextMaxSize = 15;
            text.fontSize = 15;
            text.raycastTarget = false;
            var font = NoteFont();
            if (font != null) textObject.AddComponent<ToyTextMirror>().Initialize(font, false);
            wordsOf[root] = text;
            float half = SheetSize * .5f, r = MakerSession.NoteButtonRadius, margin = .0075f; // (the drawn sheet is a little inside SheetSize)
            session.AddCollectRewrite(root, new Vector3(-half + margin + r, -half + margin + r, -.005f), 1f);
            root.localRotation *= Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(-3f, 3f));
            return root;
        }

        // A photo (white border) or a sketch (cream page) on a thin rounded card, its long side PictureSize.
        private Transform PictureView(Item item, Texture2D texture, Vector3 position, Quaternion rotation)
        {
            float aspect = texture != null && texture.height > 0 ? (float)texture.width / texture.height : 1f;
            float w = aspect >= 1f ? PictureSize : PictureSize * aspect, h = aspect >= 1f ? PictureSize / aspect : PictureSize;
            const float border = .007f;
            var root = NewView(item.kind == "sketch" ? "Collected Sketch" : "Collected Photo", item, position, rotation, new Vector2(w + 2f * border, h + 2f * border));
            var owned = ownedOf[root];
            var card = ToyMeshes.RoundedSlab(w + 2f * border, h + 2f * border, .004f, .002f, Color.white, new Color(.85f, .85f, .88f), Color.white, "CollectCard");
            owned.Add(card);
            var cardObject = new GameObject("Card", typeof(MeshFilter), typeof(MeshRenderer));
            cardObject.transform.SetParent(root, false);
            cardObject.GetComponent<MeshFilter>().sharedMesh = card;
            var renderer = cardObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = session.CardMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var rect = Canvas(root, w, h, -.0025f);
            var image = new GameObject("Image", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            image.rectTransform.SetParent(rect, false);
            image.rectTransform.anchorMin = Vector2.zero;
            image.rectTransform.anchorMax = Vector2.one;
            image.rectTransform.offsetMin = image.rectTransform.offsetMax = Vector2.zero;
            image.raycastTarget = false;
            image.texture = texture;
            if (texture != null) owned.Add(texture);
            root.localRotation *= Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(-2.5f, 2.5f));
            return root;
        }

        private Transform NewView(string name, Item item, Vector3 position, Quaternion rotation, Vector2 size)
        {
            var root = new GameObject(name + " " + views.Count).transform;
            root.SetParent(transform, false);
            root.SetPositionAndRotation(position, rotation);
            views.Add(root);
            itemOf[root] = item;
            sizeOf[root] = size;
            ownedOf[root] = new List<UnityEngine.Object>();
            return root;
        }

        private static RectTransform Canvas(Transform root, float w, float h, float z)
        {
            var canvasObject = new GameObject("Words", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvasObject.transform;
            rect.SetParent(root, false);
            rect.sizeDelta = new Vector2(w * 1000f, h * 1000f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, 0f, z);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            return rect;
        }

        private TMP_FontAsset NoteFont()
        {
            var mirror = note != null && note.title != null ? note.title.GetComponent<ToyTextMirror>() : null;
            if (mirror != null && mirror.Target != null && mirror.Target.font != null) return mirror.Target.font;
            return session != null ? session.ToyFont : null;
        }

        private void RemoveView(Transform view)
        {
            if (view == null) return;
            views.Remove(view);
            itemOf.Remove(view);
            sizeOf.Remove(view);
            wordsOf.Remove(view);
            if (pinOf.TryGetValue(view, out var pinned) && pinned != null) pinBase.Remove(pinned);
            pinOf.Remove(view);
            pinShownAt.Remove(view);
            pinFlights.Remove(view);
            if (view == editing) { editing = null; Current = editBefore; keyboard?.Cancel(); }
            if (ownedOf.TryGetValue(view, out var owned)) foreach (var o in owned) if (o != null) Destroy(o);
            ownedOf.Remove(view);
            Destroy(view.gameObject);
        }

        // Earlier things fly out of Spark into an arc in front of the person (at most ArcPerRow x ArcRows, newest in the
        // middle of the first row; a second row above it). Pictures are decoded one every other frame.
        private IEnumerator ShowCollected(List<Item> earlier)
        {
            var head = Head;
            if (head == null) yield break;
            var newest = earlier.OrderByDescending(i => i.at ?? "").Take(ArcPerRow * ArcRows).ToList();
            Vector3 forward = Flat(head.forward);
            Vector3 eyes = head.position;
            Vector3 from = spark != null ? spark.transform.position + Vector3.up * .12f : eyes + forward * .4f;
            yield return new WaitForSeconds(.6f);
            for (int i = 0; i < newest.Count; i++)
            {
                Vector3 slot = ArcSlot(i, Mathf.Min(newest.Count - (i / ArcPerRow) * ArcPerRow, ArcPerRow), eyes, forward);
                var item = newest[i];
                Transform view;
                if (item.kind == "note") view = NoteView(item, from, Facing(from));
                else
                {
                    Texture2D texture = null;
                    try
                    {
                        texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp };
                        if (!texture.LoadImage(File.ReadAllBytes(ImagePath(item)))) { Destroy(texture); texture = null; }
                    }
                    catch (Exception exception) { Debug.LogWarning("[Windups Collect] " + exception.Message); }
                    if (texture == null) continue;
                    view = PictureView(item, texture, from, Facing(from));
                    yield return null;
                }
                Shown++;
                StartCoroutine(FlyTo(view, slot, Quaternion.LookRotation((slot - eyes).normalized, Vector3.up), .5f, .2f));
                ToySounds.Play(ToySounds.Cue.Paper, from, .5f, 1.1f + .05f * i);
                yield return new WaitForSeconds(.09f);
            }
        }

        // The i-th place of the arc: row by row (inRow things in i's row), each row filled from the middle out, the
        // right one first when two are as near the middle; the second row above the first.
        public static Vector3 ArcSlot(int i, int inRow, Vector3 eyes, Vector3 forward)
        {
            int row = i / ArcPerRow, k = i % ArcPerRow;
            inRow = Mathf.Clamp(inRow, 1, ArcPerRow);
            float middle = (inRow - 1) * .5f;
            int place = Enumerable.Range(0, inRow).OrderBy(j => Mathf.Abs(j - middle)).ThenByDescending(j => j).ElementAt(Mathf.Min(k, inRow - 1));
            float angle = (place - middle) * ArcSpacing / ArcRadius * Mathf.Rad2Deg;
            Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * forward;
            return eyes + dir * ArcRadius + Vector3.down * ArcBelowEyes + Vector3.up * row * ArcRowGap;
        }

        private IEnumerator FoldIn(Transform view, float from)
        {
            for (float t = 0f; t < 1f && view != null; t += Time.deltaTime / .25f)
            {
                view.localScale = Vector3.one * Mathf.Lerp(from, 1f, Mathf.SmoothStep(0f, 1f, t));
                yield return null;
            }
            if (view != null) view.localScale = Vector3.one;
        }

        private IEnumerator FlyTo(Transform view, Vector3 to, Quaternion turn, float seconds, float fromScale = 1f)
        {
            Vector3 from = view.position;
            Quaternion fromTurn = view.rotation;
            view.localScale = Vector3.one * fromScale;
            for (float t = 0f; t < 1f && view != null; t += Time.deltaTime / seconds)
            {
                if (carry != null && carry.view == view) yield break; // taken on its way
                float e = Mathf.SmoothStep(0f, 1f, t);
                view.position = Vector3.Lerp(from, to, e) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .06f;
                view.rotation = Quaternion.Slerp(fromTurn, turn, e);
                view.localScale = Vector3.one * Mathf.Lerp(fromScale, 1f, e);
                yield return null;
            }
            if (view == null || (carry != null && carry.view == view)) yield break;
            view.SetPositionAndRotation(to, turn);
            view.localScale = Vector3.one;
            ToyFx.Puff(to, .008f, 3);
        }

        // ------------------------------------------------------------ picking things up, crumpling them

        // Grab & release (F67): a pinch within GrabReach of a thing takes it; an aimed pinch (the ray on it for
        // FarAimDwell) takes it from afar and it flies to the hand. Carried, it follows the pinch facing the eyes; let
        // go, it stays. Carried, a fist or the other hand's pinch on it crumples it.
        private void CarryFrame()
        {
            if (carry != null && !carry.mouse)
            {
                var hand = carry.hand;
                bool live = carry.view != null && hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand) && HandIO.Pinching(hand) && PinchPoint(hand, out _);
                if (carry.view != null && hand != null && HandFist(hand))
                {
                    if (carry.fistSince < 0f) carry.fistSince = Time.time;
                    else if (Time.time - carry.fistSince >= FistHold) { Crumple(carry.view); return; }
                }
                else carry.fistSince = -1f;
                if (!live) { LetGo(); return; }
                PinchPoint(hand, out var at);
                Place(carry.view, at + carry.offset);
                if (Online) PinPreview(carry.view); // F91: by a wall the pin comes out
            }
            if (editing != null) return; // (the keyboard has the hands)
#if UNITY_EDITOR
            if (EditorCarry()) return;
#endif
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) continue;
                bool raw = HandIO.RawPinching(hand);
                bool started = raw && !(wasPinching.TryGetValue(hand, out var was) && was);
                wasPinching[hand] = raw;
                if (carry != null && carry.hand == hand) continue;
                if (!PinchPoint(hand, out var at)) continue;
                // the other hand squeezes what is carried: crumpled
                if (carry != null)
                {
                    if (started && carry.view != null && Nearest(at, .06f) == carry.view) { Crumple(carry.view); return; }
                    continue;
                }
                if (session.CollectUsesHand(hand) || (Online && session.HandHolds(hand)) || (note != null && note.IsHeld)) { aimed.Remove(hand); continue; }
                Ray? ray = HandIO.Pointer(hand, out Pose pointer) ? new Ray(pointer.position, pointer.forward) : (Ray?)null;
                Transform onRay = ray.HasValue ? RayHit(ray.Value, out float rayDistance) : null;
                if (!raw)
                {
                    if (onRay == null) aimed.Remove(hand);
                    else if (!aimed.TryGetValue(hand, out var a) || a.view != onRay) aimed[hand] = (onRay, Time.time);
                }
                if (!started) continue;
                // a pinch at a post-it's Rewrite button, or the hand's ray on it, presses it
                var button = MayRewrite ? session.CollectButtonHit(at, ray, out _) : null;
                if (button != null)
                {
                    session.CollectButtonPressed(button);
                    StartRewrite(button);
                    aimed.Remove(hand);
                    return;
                }
                var near = Nearest(at, GrabReach);
                if (near != null)
                {
                    carry = new Carry { view = near, hand = hand, offset = near.position - at };
                    HandIO.BeginHold(hand);
                    ToySounds.Play(ToySounds.Cue.Paper, at, .4f, 1.4f);
                    return;
                }
                if (onRay != null && aimed.TryGetValue(hand, out var aim) && aim.view == onRay && Time.time - aim.since >= FarAimDwell)
                {
                    // from afar: it flies into the pinch (Place eases it there)
                    carry = new Carry { view = onRay, hand = hand, offset = Vector3.zero };
                    HandIO.BeginHold(hand);
                    FarTakes++;
                    ToySounds.Play(ToySounds.Cue.Whoosh, onRay.position, .3f, 1.4f);
                    aimed.Remove(hand);
                    return;
                }
            }
        }

        private void LetGo()
        {
            var view = carry?.view;
            carry = null;
            if (view == null) return;
            ToySounds.Play(ToySounds.Cue.Press, view.position, .35f, 1.6f);
            if (Online && !UseIfOnNote(view)) PinOrFree(view);
        }

        // Back online: let go on the topic note (a post-it: its words) or on the current note (a post-it: that idea; a
        // picture or sketch: a reference). Used things are gone from the collection.
        private bool UseIfOnNote(Transform view)
        {
            var item = ItemOf(view);
            if (item == null) return false;
            bool used = false;
            if (item.kind == "note" && arrival.TopicNoteNear(view.position)) used = arrival.UseCollectedWords(item.text);
            else if (session.OnCurrentNote(view.position))
            {
                if (item.kind == "note") session.UseCollectedIdea(item.text);
                else session.UseCollectedPicture(ImagePath(item), item.kind == "sketch" ? "my sketch" : "my photo", view);
                used = true;
            }
            if (!used) return false;
            item.used = true;
            Used++;
            Save(null, null);
            StartCoroutine(Fade(view));
            return true;
        }

        // ------------------------------------------------------------ F91: pinned to a wall

        private Vector2 SizeOf(Transform view) => sizeOf.TryGetValue(view, out var size) ? size : new Vector2(SheetSize, SheetSize);

        // The pin over the card's top edge, `out` in front of it (toward the reader, -Z).
        private Vector3 PinLocal(Transform view, float outward) => new Vector3(0f, SizeOf(view).y * .5f - .0085f, -outward);

        private Transform PinFor(Transform view)
        {
            if (pinOf.TryGetValue(view, out var pin) && pin != null) return pin;
            pin = session.MakeCollectPushpin(view);
            pin.localRotation = Quaternion.identity;
            pin.localPosition = PinLocal(view, PinOut);
            pinBase[pin] = pin.localScale.x;
            pin.gameObject.SetActive(false);
            pinOf[view] = pin;
            return pin;
        }

        private readonly Dictionary<Transform, float> pinBase = new Dictionary<Transform, float>();
        private float PinScale(Transform pin) => pin != null && pinBase.TryGetValue(pin, out float s) ? s : MakerSession.PushpinScale;

        // Carried within reach of a wall or the board: the pin pops out over the card and bobs; away from it, it goes.
        private void PinPreview(Transform view)
        {
            var place = session.CollectPinSpot(view.position, SizeOf(view).y * .5f, out _, out _, out _);
            var pin = PinFor(view);
            bool show = place != MakerSession.PinPlace.None;
            if (show && !pin.gameObject.activeSelf)
            {
                pin.gameObject.SetActive(true);
                pinShownAt[view] = Time.time;
                ToySounds.Play(ToySounds.Cue.Pop, view.position, .35f, 1.7f);
            }
            else if (!show && pin.gameObject.activeSelf) pin.gameObject.SetActive(false);
            if (!show) return;
            float k = Mathf.Clamp01((Time.time - (pinShownAt.TryGetValue(view, out float at) ? at : 0f)) / .16f);
            float pop = 1f + 2.70158f * Mathf.Pow(k - 1f, 3f) + 1.70158f * Mathf.Pow(k - 1f, 2f); // ease out, a little past and back
            pin.localScale = Vector3.one * PinScale(pin) * Mathf.Max(.01f, pop);
            pin.localPosition = PinLocal(view, PinOut + .004f * Mathf.Sin(Time.time * 9f));
        }

        // Let go: by a wall or the board it is pinned there; elsewhere it is free (a pin it had goes).
        private void PinOrFree(Transform view)
        {
            var item = ItemOf(view);
            if (item == null) return;
            var place = session.CollectPinSpot(view.position, SizeOf(view).y * .5f, out var pose, out string anchor, out _);
            if (place == MakerSession.PinPlace.None)
            {
                if (pinOf.TryGetValue(view, out var pin) && pin != null) pin.gameObject.SetActive(false);
                if (item.pinOn == null) return;
                item.pinOn = item.pinAnchor = null;
                item.pinLocal = item.pinTurn = item.pinWorld = item.pinWorldTurn = null;
                Save(null, null);
                Debug.Log("[Windups Collect] unpinned: " + (item.text ?? item.image));
                return;
            }
            pose.rotation *= Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(-3f, 3f));
            item.pinOn = place == MakerSession.PinPlace.Board ? "board" : "wall";
            item.pinAnchor = anchor;
            session.CollectPinLocal(place, pose, anchor, out item.pinLocal, out item.pinTurn);
            item.pinWorld = new[] { pose.position.x, pose.position.y, pose.position.z };
            item.pinWorldTurn = new[] { pose.rotation.x, pose.rotation.y, pose.rotation.z, pose.rotation.w };
            Save(null, null);
            PinsPut++;
            if (pinFlights.TryGetValue(view, out var flight) && flight != null) StopCoroutine(flight);
            pinFlights[view] = StartCoroutine(PinTo(view, pose));
            Debug.Log("[Windups Collect] pinned to the " + item.pinOn + ": " + (item.text ?? item.image));
        }

        // Flat onto the wall, then the pin pushes in (click, a puff).
        private IEnumerator PinTo(Transform view, Pose pose)
        {
            var pin = PinFor(view);
            if (!pin.gameObject.activeSelf) { pin.gameObject.SetActive(true); pin.localScale = Vector3.one * PinScale(pin); }
            Vector3 from = view.position;
            Quaternion fromTurn = view.rotation;
            for (float t = 0f; t < 1f; t += Time.deltaTime / .18f)
            {
                if (view == null || (carry != null && carry.view == view)) yield break;
                float e = Mathf.SmoothStep(0f, 1f, t);
                view.SetPositionAndRotation(Vector3.Lerp(from, pose.position, e), Quaternion.Slerp(fromTurn, pose.rotation, e));
                yield return null;
            }
            if (view == null || (carry != null && carry.view == view)) yield break;
            view.SetPositionAndRotation(pose.position, pose.rotation);
            for (float t = 0f; t < 1f; t += Time.deltaTime / .1f)
            {
                if (view == null || pin == null || (carry != null && carry.view == view)) yield break;
                pin.localPosition = PinLocal(view, Mathf.Lerp(PinOut, PinIn, t * t));
                yield return null;
            }
            if (pin == null || view == null) yield break;
            pin.localPosition = PinLocal(view, PinIn);
            pin.localScale = Vector3.one * PinScale(pin);
            ToySounds.Play(ToySounds.Cue.Press, pin.position, .55f, 1.9f);
            ToyFx.Puff(pin.position, .006f, 3);
            pinFlights.Remove(view);
        }

        // On coming back: a pinned thing is put straight back on its wall, its pin in (false: no place for it now).
        private bool RestorePinned(Item item)
        {
            if (item?.pinOn == null || !session.CollectPinPose(item.pinOn, item.pinAnchor, item.pinLocal, item.pinTurn, item.pinWorld, item.pinWorldTurn, out var pose)) return false;
            Transform view;
            if (item.kind == "note") view = NoteView(item, pose.position, pose.rotation);
            else
            {
                Texture2D texture = null;
                try
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp };
                    if (!texture.LoadImage(File.ReadAllBytes(ImagePath(item)))) { Destroy(texture); texture = null; }
                }
                catch (Exception exception) { Debug.LogWarning("[Windups Collect] " + exception.Message); }
                if (texture == null) return false;
                view = PictureView(item, texture, pose.position, pose.rotation);
            }
            view.SetPositionAndRotation(pose.position, pose.rotation);
            var pin = PinFor(view);
            pin.gameObject.SetActive(true);
            pin.localScale = Vector3.one * PinScale(pin);
            pin.localPosition = PinLocal(view, PinIn);
            StartCoroutine(FoldIn(view, .05f));
            PinsRestored++;
            Shown++;
            return true;
        }

        private IEnumerator Fade(Transform view)
        {
            Vector3 start = view.localScale;
            for (float t = 0f; t < 1f && view != null; t += Time.deltaTime / .3f)
            {
                view.localScale = start * (1f - Mathf.SmoothStep(0f, 1f, t));
                yield return null;
            }
            RemoveView(view);
        }

        // Crumpled into a paper ball that flies to Fixer: thrown away (the item and its picture are deleted).
        private void Crumple(Transform view)
        {
            var item = ItemOf(view);
            Vector3 at = view.position;
            if (carry != null && carry.view == view) carry = null;
            RemoveView(view);
            if (item != null)
            {
                data.items.Remove(item);
                if (!string.IsNullOrEmpty(item.image)) { string path = ImagePath(item); Task.Run(() => { try { File.Delete(path); } catch { } }); }
                Save(null, null);
            }
            Crumpled++;
            ToySounds.Play(ToySounds.Cue.Paper, at, .8f, .8f);
            var material = session.CardMaterial;
            if (material == null) return;
            var ball = PaperBall.Make(transform, material, UnityEngine.Random.Range(0, 4));
            ball.transform.position = at;
            StartCoroutine(BallToFixer(ball.transform));
        }

        private IEnumerator BallToFixer(Transform ball)
        {
            Vector3 from = ball.position;
            for (float t = 0f; t < 1f && ball != null; t += Time.deltaTime / .55f)
            {
                Vector3 to = fixer != null ? fixer.transform.position + Vector3.up * .1f : from + Vector3.down * .3f;
                ball.position = Vector3.Lerp(from, to, t) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .12f;
                ball.Rotate(400f * Time.deltaTime, 250f * Time.deltaTime, 0f);
                yield return null;
            }
            if (ball == null) yield break;
            ToyFx.Puff(ball.position, .01f, 4);
            Destroy(ball.gameObject);
            if (fixer != null) session.OfflineReact(Crew.Fixer, "Bye!");
        }

        private static bool HandFist(Hand hand)
        {
            if (hand == null || !HandIO.Joint(hand, HandJointId.HandPalm, out Pose palm)) return false;
            float sum = 0f;
            foreach (var joint in new[] { HandJointId.HandMiddleTip, HandJointId.HandRingTip, HandJointId.HandPinkyTip })
            {
                if (!HandIO.Joint(hand, joint, out Pose tip)) return false;
                sum += Vector3.Distance(tip.position, palm.position);
            }
            return sum / 3f < .048f;
        }

        private void Place(Transform view, Vector3 at)
        {
            view.position = Vector3.Lerp(view.position, at, 1f - Mathf.Exp(-24f * Time.deltaTime));
            view.rotation = Quaternion.Slerp(view.rotation, Facing(view.position), 1f - Mathf.Exp(-10f * Time.deltaTime));
        }

        private static bool PinchPoint(Hand hand, out Vector3 at)
        {
            at = default;
            if (!HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose index) || !HandIO.Joint(hand, HandJointId.HandThumbTip, out Pose thumb)) return false;
            at = (index.position + thumb.position) * .5f;
            return true;
        }

        // The thing nearest `at` within `reach` (metres) of its surface; where things overlap (one still folding or
        // flying over its neighbour) the one whose middle is nearest wins.
        public Transform Nearest(Vector3 at, float reach)
        {
            Transform best = null;
            float bestScore = float.PositiveInfinity;
            foreach (var view in views)
            {
                if (view == null) continue;
                Vector3 local = view.InverseTransformPoint(at); // (local units: the scale is taken out, so scale back)
                var size = sizeOf.TryGetValue(view, out var s2) ? s2 : new Vector2(SheetSize, SheetSize);
                float s = view.lossyScale.x;
                float dx = Mathf.Max(0f, Mathf.Abs(local.x) - size.x * .5f), dy = Mathf.Max(0f, Mathf.Abs(local.y) - size.y * .5f), dz = Mathf.Max(0f, Mathf.Abs(local.z) - .006f);
                float gap = new Vector3(dx, dy, dz).magnitude * s;
                if (gap > reach) continue;
                float score = gap + .1f * Vector3.Distance(at, view.position);
                if (score < bestScore) { bestScore = score; best = view; }
            }
            return best;
        }

        // The thing a ray meets first (on its face, from either side).
        public Transform RayHit(Ray ray, out float distance)
        {
            Transform best = null;
            distance = float.PositiveInfinity;
            foreach (var view in views)
            {
                if (view == null || (carry != null && carry.view == view)) continue;
                var plane = new Plane(view.forward, view.position);
                if (!plane.Raycast(ray, out float d) || d <= 0f || d >= distance) continue;
                Vector3 local = view.InverseTransformPoint(ray.GetPoint(d));
                var size = sizeOf.TryGetValue(view, out var s2) ? s2 : new Vector2(SheetSize, SheetSize);
                if (Mathf.Abs(local.x) > size.x * .5f + .01f || Mathf.Abs(local.y) > size.y * .5f + .01f) continue;
                distance = d;
                best = view;
            }
            return best;
        }

#if UNITY_EDITOR
        private static bool MouseDown(out Ray ray)
        {
            ray = default;
            if (UnityEngine.XR.XRSettings.isDeviceActive || Camera.main == null || !(UnityEngine.InputSystem.Mouse.current is { } mouse) || !mouse.leftButton.wasPressedThisFrame) return false;
            ray = Camera.main.ScreenPointToRay(mouse.position.ReadValue());
            return true;
        }

        // Editor: drag a thing with the mouse; X while dragging crumples it.
        private bool EditorCarry()
        {
            if (UnityEngine.XR.XRSettings.isDeviceActive || HandIO.Active || Camera.main == null || !(UnityEngine.InputSystem.Mouse.current is { } mouse)) return false;
            Ray ray = Camera.main.ScreenPointToRay(mouse.position.ReadValue());
            if (carry != null && carry.mouse)
            {
                if (carry.view != null && UnityEngine.InputSystem.Keyboard.current?.xKey.wasPressedThisFrame == true && !WindupsKeyboard.Typing) { Crumple(carry.view); return true; }
                if (!mouse.leftButton.isPressed || carry.view == null) { LetGo(); return true; }
                Place(carry.view, ray.GetPoint(carry.distance) + carry.offset);
                return true;
            }
            if (carry != null || !mouse.leftButton.wasPressedThisFrame) return false;
            var button = MayRewrite ? session.CollectButtonHit(Vector3.positiveInfinity, ray, out _) : null;
            if (button != null) { session.CollectButtonPressed(button); StartRewrite(button); return true; }
            var hit = RayHit(ray, out float d);
            if (hit == null) return false;
            carry = new Carry { view = hit, mouse = true, distance = d, offset = hit.position - ray.GetPoint(d) };
            return true;
        }
#endif

        // ------------------------------------------------------------ saving (content only, off the main thread)

        private static string ImagePath(Item item) => string.IsNullOrEmpty(item?.image) ? "" : Path.Combine(ImageDir, item.image);

        private static Data Load()
        {
            try { if (File.Exists(SavePath)) return JsonConvert.DeserializeObject<Data>(File.ReadAllText(SavePath)) ?? new Data(); }
            catch (Exception exception) { Debug.LogWarning("[Windups Collect] " + exception.Message); }
            return new Data();
        }

        // The list (and a new picture's bytes) written in a Task.
        private void Save(string imageName, byte[] imageBytes)
        {
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            string path = SavePath, dir = ImageDir;
            Task.Run(() =>
            {
                lock (saveLock)
                {
                    try
                    {
                        if (imageBytes != null && !string.IsNullOrEmpty(imageName))
                        {
                            Directory.CreateDirectory(dir);
                            File.WriteAllBytes(Path.Combine(dir, imageName), imageBytes);
                        }
                        File.WriteAllText(path, json);
                    }
                    catch (Exception exception) { Debug.LogWarning("[Windups Collect] " + exception.Message); }
                }
            });
        }

        // ------------------------------------------------------------ checks

        public void SimulateTake() { if (Current == Step.Offering) Take(); }
        public void SimulateType(string text, bool done) { if (Current == Step.Typing || Current == Step.Editing) keyboard.Simulate(text, done); }
        public void SimulateRewrite(Transform view) { session.CollectButtonPressed(view); StartRewrite(view); }
        public void SimulateCancel() { keyboard?.Cancel(); }
        public void SimulateKeep() { if (Current == Step.Placing) quietFor = SettleSeconds; }
        public void SimulateCarry(Transform view, Vector3 to) { if (view != null) view.SetPositionAndRotation(to, Facing(to)); }
        public void SimulateCrumple(Transform view) { if (view != null) Crumple(view); }
        public void SimulateLetGoAt(Transform view, Vector3 at)
        {
            if (view == null) return;
            view.SetPositionAndRotation(at, Facing(at));
            if (Online && !UseIfOnNote(view)) PinOrFree(view);
        }
        public bool SessionCollecting => session != null && session.Collecting;
    }
}
