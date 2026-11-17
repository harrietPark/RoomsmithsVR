using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    // F56 (2026-11-13, the user's own flow): the shape is COLLECTED, never taken from one input.
    //  - Everything given for the shape becomes a card: what was said (a words card by the mic), a picture taken
    //    with Spark's frame (a photo card out of the frame), a drawing from Maker's sketchbook (a drawing card out
    //    of the page, tidied or not). One is enough; any number, in any order.
    //  - A card counts only once the person puts it on the note in the ring (grab it near or far and let go over
    //    the ring): it sticks beside the note and the ring is inked round again. Taken off again, it does not count. Cards never
    //    put there are simply not used. At most 3 photos and 2 drawings on the note; words as many as wanted.
    //  - When a card lands on the note, the teammate who made it asks what it is for ("the whole shape, or one
    //    part?"); the person says it or taps a pill, and the answer is written on the card's tab. Said beforehand
    //    ("this is the handle"), it is not asked.
    //  - The first card on the note brings the Done pill and Maker's "say done when that's all". Done (said or
    //    pressed): Maker reads the cards back, each hopping as it is named, then draws the three shape sketches
    //    from the cards on the note only (two whole shapes: the sketches show both readings).
    //  - Nothing moves on by itself in the shape step any more.
    // A card taken off the note or crumpled goes to Fixer's basket: MakerSession.ShapeTrash.cs.
    public sealed partial class MakerSession
    {
        public enum CardKind { Words, Photo, Drawing }

        public sealed class ShapeCard
        {
            public int id;
            public CardKind kind;
            public string text = "", raw = "", role = "", roleRaw = "", source = "";
            public byte[] jpeg;
            public Texture2D texture;
            public bool kept, crumpled, peeled, asked;
            public List<List<Vector2>> strokes;   // F86: a drawing card's lines (page coordinates), for Maker's polish
            public bool polishAsked, polished;
            public Transform root, tab;
            public TextMeshProUGUI body, tabText;
            public PaperBall ball;
            public float width, height, born, hopAt = -10f;
            public Vector3 home;
            public bool HasImage => kind != CardKind.Words;
            public Vector3 Position => crumpled && ball != null ? ball.transform.position : root != null ? root.position : home;
        }

        // Checks of other features only (never the app): a card goes on the note by itself and the collecting ends
        // InputSettle seconds after the last card, as one input used to end the shape step before F56.
        public static bool ShapeCardsAuto;
        private float autoDoneAt = -1f;

        public const int MaxPhotos = 3, MaxDrawings = 2;
        public const float KeptScale = .7f;
        public const string WholeShape = "Whole shape", DetailRole = "Detail";

        public readonly List<ShapeCard> ShapeCards = new List<ShapeCard>();
        public IEnumerable<ShapeCard> KeptCards => ShapeCards.Where(c => c.kept && !c.crumpled);
        public int KeptCount => ShapeCards.Count(c => c.kept && !c.crumpled);
        public ShapeCard CarriedCard => carriedCard;
        public ShapeCard RoleCard => roleCard;
        public bool ShapeDoneShown => shapeDone != null && shapeDone.gameObject.activeSelf;
        public Transform ShapeDoneView => shapeDone;
        public bool ShapeSummarising => summarising;
        public string LastSummary { get; private set; } = "";
        public int CardCoaches { get; private set; }
        public int ShapeDonePresses { get; private set; }

        private ShapeCard carriedCard, roleCard;
        private Hand cardHand;
        private float cardFar = -1f;
        private Vector3 cardOffset;
        private bool cardMouse, cardOverRing, doneLineSaid, summarising, donePressing;
        private Plane cardPlane;
        private Vector3? cardTarget; // tests: where the carried card is
        private readonly List<(Vector3 at, float time)> cardPath = new List<(Vector3, float)>();
        private Transform shapeDone, shapeDonePill;
        private float doneShownAt, donePulseAt = -10f;
        private readonly FingerPress donePress = new FingerPress(); // F60
        private int cardSeq, keepsThisStep, summaryToken;

        // ------------------------------------------------------------ making a card

        private Crew CardOwner(ShapeCard card) => card.kind == CardKind.Photo ? Crew.Spark : Crew.Maker;

        private ShapeCard AddShapeCard(CardKind kind, string text, string raw, byte[] jpeg, Texture2D texture, Vector3 from, Vector3 near)
        {
            var card = new ShapeCard { id = ++cardSeq, kind = kind, text = text ?? "", raw = raw ?? "", jpeg = jpeg, texture = texture, born = Time.time };
            BuildCard(card);
            card.home = CardRest(near);
            card.root.SetPositionAndRotation(from, SpeechBubbleDock.FacingAt(from));
            card.root.localScale = Vector3.one * .15f;
            ShapeCards.Add(card);
            ShapeInputs++;
            lastInputAt = Time.time;
            summaryToken++;
            StopTutorial();
            ToySounds.Play(ToySounds.Cue.Pop, from, .45f, 1.25f);
            ToyFx.Puff(from, .006f, 3);
            return card;
        }

        private void BuildCard(ShapeCard card)
        {
            float w, h;
            if (card.HasImage)
            {
                float aspect = card.texture != null && card.texture.height > 0 ? card.texture.width / (float)card.texture.height : 1f;
                w = aspect >= 1f ? .09f : Mathf.Max(.045f, .09f * aspect);
                h = aspect >= 1f ? Mathf.Max(.045f, .09f / aspect) : .09f;
            }
            else { w = .12f; h = .055f; }
            card.width = w;
            card.height = h;
            var root = new GameObject("Shape Card " + card.id + " " + card.kind).transform;
            root.SetParent(transform, false);
            Color face = card.HasImage ? Color.white : new Color(1f, .96f, .8f), edge = card.HasImage ? new Color(.85f, .85f, .88f) : new Color(.9f, .82f, .55f);
            var slab = ToyMeshes.RoundedSlab(w + .012f, h + .012f, .008f, .003f, face, edge, face, "ShapeCard");
            owned.Add(slab);
            Part(root, "Card", slab, Vector3.zero, 0f);
            var canvas = new GameObject("Face", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(root, false);
            rect.sizeDelta = new Vector2((w + .012f) * 1000f, (h + .012f) * 1000f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, 0f, -.0022f);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            if (card.HasImage)
            {
                var image = new GameObject("Image", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
                image.rectTransform.SetParent(rect, false);
                image.rectTransform.sizeDelta = new Vector2(w * 1000f, h * 1000f);
                image.texture = card.texture;
                image.raycastTarget = false;
            }
            else
            {
                card.body = HatIdeaCard.Label(rect, font, 5f, 4f, (w + .012f) * 1000f - 10f, (h + .012f) * 1000f - 8f, 6f, 13f, new Color(.15f, .15f, .18f), FontStyles.Normal, TextAlignmentOptions.Center);
                card.body.text = card.text;
            }
            // The tab that says what the card is for (shown once it is known).
            Color mint = new Color(.8f, .97f, .86f);
            card.tab = PaperTag("Role", .072f, .02f, mint, out card.tabText, 11f, false); // F77: a paper label, not a pill
            card.tab.SetParent(root, false);
            card.tab.localPosition = new Vector3(-w * .5f + .032f, h * .5f + .014f, -.0015f);
            card.tab.gameObject.SetActive(false);
            card.root = root;
            ShowRole(card);
        }

        private void ShowRole(ShapeCard card)
        {
            if (card.tab == null) return;
            bool known = !string.IsNullOrEmpty(card.role);
            card.tab.gameObject.SetActive(known);
            if (known) card.tabText.text = card.role;
        }

        // A free place to wait near `near` (never on top of another waiting card).
        // (2026-11-13: a words card came up behind a shelf beside the mic.) Where the eyes see it: nothing of the
        // room between the head and it, and not inside the room's things; else in the air in front of the person.
        private Vector3 CardRest(Vector3 near)
        {
            Vector3 front = FrontPoint(.38f, -.2f, out Vector3 side);
            foreach (Vector3 from in new[] { near, front })
                foreach (float s in new[] { 0f, .13f, -.13f, .26f, -.26f, .39f, -.39f })
                {
                    Vector3 p = from + side * s;
                    if (ShapeCards.Any(c => !c.kept && !c.crumpled && Vector3.Distance(c.home, p) < .115f)) continue;
                    if (CardSeen(p)) return p;
                }
            return front + Vector3.up * .12f;
        }

        private bool CardSeen(Vector3 p)
        {
            var room = arrival != null ? arrival.handoff.meeting.environment : null;
            if (room == null || Head == null) return true;
            int mask = room.GeometryMask;
            if (Physics.CheckSphere(p, .04f, mask, QueryTriggerInteraction.Ignore)) return false;
            return !Physics.Linecast(Head.position, p, mask, QueryTriggerInteraction.Ignore);
        }

        private static bool Latin(string text) => !string.IsNullOrWhiteSpace(text) && text.All(c => c < 0x250);
        private static string Shorten(string text, int most) => text.Length <= most ? text : text.Substring(0, most - 1).TrimEnd() + "...";

        // What was said for the shape: a words card by the mic.
        private ShapeCard AddWordsCard(string said)
        {
            string clean = MakerRoles.Clean(said);
            bool latin = Latin(clean);
            Vector3 mic = MicPoint();
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - mic, Vector3.up).normalized : Vector3.back;
            var card = AddShapeCard(CardKind.Words, latin ? Shorten(clean, 90) : "...", said, null, null, mic + Vector3.up * .06f, mic + Vector3.up * .2f + toUser * .03f); // (well above the mic: a pinch within 7 cm of the mic picks the mic up)
            card.role = RoleInWords(said.ToLowerInvariant()) ?? "";
            ShowRole(card);
            Talk.Add("The person's words for the shape became a card: " + said);
            // Words the headset font cannot show are written on the card as Maker understood them, in English.
            if (!latin) StartCoroutine(WriteCardInEnglish(card));
            CoachCard(card, "Got it!");
            return card;
        }

        private IEnumerator WriteCardInEnglish(ShapeCard card)
        {
            string json = null;
            yield return Ask(Crew.Maker, "card", new { words = card.raw, making = Need }, r => json = r, false);
            if (card.root == null) yield break;
            string english = null;
            if (json != null)
                try { english = MakerRoles.Clean(JsonConvert.DeserializeObject<NoteReply>(json)?.say ?? ""); } catch { }
            card.text = !string.IsNullOrWhiteSpace(english) && Latin(english) ? Shorten(english, 90) : "(your words)";
            if (card.body != null) card.body.text = card.text;
        }

        // A picture as a card (Spark's frame, or something Spark looked at): `label` names the thing when known.
        private ShapeCard AddPhotoCard(byte[] jpeg, Texture2D texture, string label, string source, Vector3 from, Vector3 near)
        {
            if (texture == null && jpeg != null)
            {
                texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp };
                if (!texture.LoadImage(jpeg)) { Destroy(texture); return null; }
            }
            var card = AddShapeCard(CardKind.Photo, label ?? "", "", jpeg, texture, from, near);
            card.source = source ?? "";
            ShapeRef = jpeg; // (what older code reads as "the shape photo": the newest one)
            Talk.Add("A picture for the shape became a card" + (string.IsNullOrEmpty(label) ? "" : " (" + label + ")") + ": " + source);
            Body(Crew.Spark)?.Play("Peek");
            Heard();
            CoachCard(card, "Snapped!");
            return card;
        }

        // ShowPicture: the frame's picture in this step.
        private void FramePicture(byte[] jpeg, Color32[] pixels, int width, int height, string source)
        {
            if (Current != Step.ShapeInput) { InputPicture(jpeg, null); return; }
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels);
            texture.Apply(false);
            Vector3 from = showFrame.position, near = frameButton.position + (FrameLying ? Vector3.up * .11f : showFrame.up * .08f);
            AddPhotoCard(jpeg, texture, null, source, from, near);
        }

        // TakeDrawing (MakerSession.Tools.cs) in the shape step: the page becomes a drawing card, and the book goes
        // back to Maker, who holds it out again.
        private IEnumerator DrawingToCard(WindupCharacter maker)
        {
            paperInFlight = true;
            Drawing = false;
            penDown = false;
            var jpeg = RasterDrawing(drawn);
            DrawingJpeg = jpeg;
            Talk.Add("The person drew a shape in Maker's sketchbook" + (Tidied > 0 ? " (lines tidied)." : "."));
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp };
            texture.LoadImage(jpeg);
            Vector3 page = paper.position;
            var card = AddShapeCard(CardKind.Drawing, "", "", jpeg, texture, page + Vector3.up * .01f, page + Vector3.up * .13f);
            card.strokes = drawn.Select(s => new List<Vector2>(s)).ToList(); // F86: kept for Maker's polish (the page is cleared below)
            ToySounds.Play(ToySounds.Cue.Peel, page, .5f, 1.2f);
            Vector3 from = paper.position;
            Quaternion fromRot = paper.rotation;
            for (float t = 0f; t < 1f && maker != null; t += Time.deltaTime / .45f)
            {
                paper.position = Vector3.Lerp(from, maker.transform.position + Vector3.up * .08f, Mathf.SmoothStep(0f, 1f, t)) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .1f;
                paper.rotation = Quaternion.Slerp(fromRot, maker.transform.rotation, t);
                paper.localScale = Vector3.one * Mathf.Lerp(1f, .25f, t);
                yield return null;
            }
            paper.gameObject.SetActive(false);
            paperInFlight = false;
            // F82 (Quest: "I crumpled the sketch into the basket but it stayed in the sketchbook"): the drawing is the card
            // now; the book opens on a fresh page next time.
            ClearPaper();
            // F79: Maker asks for the drawing first (its book comes out again with Add more).
            if (Current == Step.ShapeInput && !GiveCardsToMaker) StartOfferingBook(); // free to draw another
            CoachCard(card, "Here's your drawing.");
        }

        // F79 (Quest 2026-11-14: "where do I put my drawing? Maker should ask for it - and everything else I give, words,
        // pictures, goes to Maker; then 'anything else, or done?' with buttons for that"): a card is given to Maker (let
        // go near it, or aimed at it from afar). Maker keeps the cards in a small fan beside itself and asks
        // "Anything else, or is that all?" (Add more | That's all). false = the F56/F72 note (older checks).
        public static bool GiveCardsToMaker = true;
        public const float GiveReach = .2f, GivenScale = .45f;
        public const string AddMoreLabel = "Add more", ThatsAllLabel = "That's all", AnythingElseLine = "Got it! Anything else, or is that all?";

        // Where Maker takes a card: in front of it, about hand height.
        private bool MakerTakes(out Vector3 at)
        {
            at = Vector3.zero;
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null) return false;
            Vector3 feet = maker.transform.position;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - feet, Vector3.up) : Vector3.back;
            toUser = toUser.sqrMagnitude > 1e-4f ? toUser.normalized : Vector3.back;
            at = feet + Vector3.up * .09f + toUser * .05f;
            return true;
        }

        // The side of Maker away from the other teammates (as seen by the person), where its cards stand.
        private Vector3 FanSide(WindupCharacter maker)
        {
            Vector3 feet = maker.transform.position;
            Vector3 toEyes = Head != null ? Vector3.ProjectOnPlane(Head.position - feet, Vector3.up) : Vector3.back;
            toEyes = toEyes.sqrMagnitude > 1e-5f ? toEyes.normalized : Vector3.back;
            Vector3 side = Vector3.Cross(toEyes, Vector3.up);
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (var pair in cast) if (pair.Key != Crew.Maker && pair.Value != null) { sum += pair.Value.transform.position; count++; }
            if (count > 0 && Vector3.Dot(sum / count - feet, side) > 0f) side = -side;
            return side;
        }

        private bool NearMaker(Vector3 at) => MakerTakes(out var hands) && Vector3.Distance(at, hands) < GiveReach;

        // Where a card counts: given to Maker (F79), or on the note (F56/F72).
        private bool CardGoesIn(Vector3 at) => GiveCardsToMaker ? NearMaker(at) : OverRing(at);

        // Whoever made the card says to put it on the note: a full line the first time, a short one after, and
        // once the person has done it twice only a look at the note (never the same line again and again).
        // F79: Maker asks for it ("Give it to me!") with its hand out.
        private void CoachCard(ShapeCard card, string first)
        {
            if (card == null) return;
            Crew who = CardOwner(card);
            if (ShapeCardsAuto) { AutoKeep(card); Ack(who, first); return; }
            if (GiveCardsToMaker)
            {
                CardCoaches++;
                if (who != Crew.Maker) Ack(who, first);
                Say(Crew.Maker, (who == Crew.Maker ? first + " " : "") + (keepsThisStep >= 2 ? "Give it to me!" : "Give it to me! Bring it close."));
                Body(Crew.Maker)?.Play("HandRaise");
                LookAt(Crew.Maker, card.home, 1.6f);
                return;
            }
            Vector3 note = RingPose(out var at, out _, out _, out _) ? at : card.home;
            if (keepsThisStep >= 2) { Ack(who, first); LookAt(who, note, 1.6f); return; }
            CardCoaches++;
            if (CardCoaches == 1)
            {
                Say(who, first + " Like it? Put it on your note.");
                Body(who)?.Play("HandRaise");
                LookAt(who, card.home, 1.2f);
                StartCoroutine(After(1.3f, () => LookAt(who, note, 1.6f)));
                arrival.RingNoteIn(); // the ring shows where
            }
            else { Ack(who, first + " On your note?"); LookAt(who, note, 1.6f); }
        }

        private void AutoKeep(ShapeCard card)
        {
            card.kept = true;
            if (string.IsNullOrEmpty(card.role) && !ShapeCards.Any(c => c != card && c.kept && c.role == WholeShape)) card.role = WholeShape;
            ShowRole(card);
            keepsThisStep++;
            UpdateShapeWords();
            autoDoneAt = Time.time + InputSettle;
        }

        // The words on the note, as the sketch maker reads them (kept up to date as cards come and go).
        private void UpdateShapeWords() =>
            ShapeWords = string.Join("; ", KeptCards.Where(c => c.kind == CardKind.Words).Select(c => (string.IsNullOrEmpty(c.role) ? "" : c.role + ": ") + c.raw));

        // ------------------------------------------------------------ picking a card up, carrying it, letting go

        private ShapeCard CardAt(Vector3 point)
        {
            if (float.IsInfinity(point.x)) return null;
            ShapeCard best = null;
            float bestD = float.MaxValue;
            foreach (var c in ShapeCards)
            {
                float d;
                if (c.crumpled) { if (c.ball == null) continue; d = Vector3.Distance(point, c.ball.transform.position); if (d > .045f) continue; }
                else
                {
                    if (c.root == null) continue;
                    Vector3 l = c.root.InverseTransformPoint(point);
                    if (Mathf.Abs(l.x) > c.width * .5f + .018f || Mathf.Abs(l.y) > c.height * .5f + .018f || Mathf.Abs(l.z) > .05f) continue;
                    d = Vector3.Distance(point, c.root.position);
                }
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        public ShapeCard CardOnRay(Ray ray, out float distance)
        {
            distance = float.PositiveInfinity;
            ShapeCard best = null;
            foreach (var c in ShapeCards)
            {
                if (c.crumpled)
                {
                    if (c.ball == null) continue;
                    Vector3 to = c.ball.transform.position - ray.origin;
                    float along = Vector3.Dot(to, ray.direction);
                    if (along < .1f || along > 3.5f || along >= distance || Vector3.Distance(ray.GetPoint(along), c.ball.transform.position) > .03f + along * .01f) continue;
                    distance = along;
                    best = c;
                    continue;
                }
                if (c.root == null) continue;
                if (!new Plane(c.root.forward, c.root.position).Raycast(ray, out float d) || d < .1f || d > 3.5f || d >= distance) continue;
                Vector3 l = c.root.InverseTransformPoint(ray.GetPoint(d));
                if (Mathf.Abs(l.x) > c.width * .5f + .008f || Mathf.Abs(l.y) > c.height * .5f + .008f) continue;
                distance = d;
                best = c;
            }
            return best;
        }

        // A new pinch: the Done pill, or a card (near; the hand's ray when `ray` is given).
        private bool ShapeCardPinch(Hand hand, Vector3 point, Ray? ray)
        {
            if (carriedCard != null) return false;
            if (ShapeDonePinch(point, ray)) return true;
            if (ShapeCards.Count == 0) return false;
            float far = -1f;
            var card = CardAt(point);
            if (card == null && ray.HasValue) card = CardOnRay(ray.Value, out far);
            if (card == null) return false;
            CarryCard(card, hand, point, far, ray);
            return true;
        }

        private void CarryCard(ShapeCard card, Hand hand, Vector3 point, float far, Ray? ray)
        {
            carriedCard = card;
            cardHand = hand;
            cardFar = far;
            cardMouse = hand == null && !cardTarget.HasValue;
            cardOffset = far > 0f || float.IsInfinity(point.x) ? Vector3.zero : card.Position - point;
            if (hand == null && ray.HasValue) cardPlane = new Plane(-ray.Value.direction, card.Position);
            cardPath.Clear();
            cardOverRing = false;
            if (card.crumpled && card.ball != null) card.ball.Hold();
            if (card.kept)
            {
                // Off the note: it does not count any more (Fixer's basket comes, MakerSession.ShapeTrash.cs).
                card.kept = false;
                card.peeled = true;
                summaryToken++;
                UpdateShapeWords();
                ToySounds.Play(ToySounds.Cue.Peel, card.Position, .5f, 1.3f);
                Talk.Add("The person took a " + CardKindName(card) + " card off the note.");
                if (roleCard == card) DropRoleQuestion();
                if (KeptCount == 0) HideShapeDone();
            }
            else ToySounds.Play(ToySounds.Cue.Paper, card.Position, .4f, 1.2f);
            Heard();
        }

        private void CardCarryFrame()
        {
            var card = carriedCard;
            if (card == null) return;
            bool hand = cardHand != null && cardHand.isActiveAndEnabled && HandIO.Connected(cardHand) && (HandIO.Pinching(cardHand) || HandFist(cardHand));
            bool mouse = false;
            Ray? mouseRay = null;
#if UNITY_EDITOR
            if (cardMouse && UnityEngine.InputSystem.Mouse.current is { } m && Camera.main != null)
            {
                mouse = m.leftButton.isPressed;
                mouseRay = Camera.main.ScreenPointToRay(m.position.ReadValue());
                if (mouse && UnityEngine.InputSystem.Keyboard.current is { } keys && keys.xKey.wasPressedThisFrame) Crumple(card);
            }
#endif
            if (!cardTarget.HasValue && !hand && !mouse) { DropCard(); return; }
            Vector3 target;
            Ray? aim = null;
            if (cardTarget.HasValue) target = cardTarget.Value;
            else if (cardHand != null && cardFar > 0f && HandIO.Pointer(cardHand, out Pose pointer)) { aim = new Ray(pointer.position, pointer.forward); target = aim.Value.GetPoint(cardFar); }
            else if (cardHand != null) target = PinchPoint(cardHand) + cardOffset;
            else if (mouseRay.HasValue && cardPlane.Raycast(mouseRay.Value, out float d)) { aim = mouseRay; target = mouseRay.Value.GetPoint(d); }
            else target = card.Position;
            // F79: aimed at Maker from afar, the card comes to its hands.
            if (aim.HasValue && !card.crumpled && GiveCardsToMaker && MakerTakes(out var hands))
            {
                float along = Vector3.Dot(hands - aim.Value.origin, aim.Value.direction);
                if (along > .1f && Vector3.Distance(aim.Value.GetPoint(along), hands) < .14f) target = hands;
            }
            // Aimed at the ring from afar: the card comes over it.
            else if (aim.HasValue && !card.crumpled && RingPose(out var at, out var noteFacing, out var centre, out float radius))
            {
                float along = Vector3.Dot(centre - aim.Value.origin, aim.Value.direction);
                if (along > .1f && Vector3.Distance(aim.Value.GetPoint(along), centre) < radius + .1f) target = NoteOnWall ? at - noteFacing * Vector3.forward * .04f : at + Vector3.up * .06f;
            }
            if (aim.HasValue && card.crumpled && TrashShown)
            {
                float along = Vector3.Dot(TrashPoint - aim.Value.origin, aim.Value.direction);
                if (along > .1f && Vector3.Distance(aim.Value.GetPoint(along), TrashPoint) < .1f) target = TrashPoint + Vector3.up * .05f;
            }
            float blend = cardTarget.HasValue ? 1f : 1f - Mathf.Exp(-Time.deltaTime * 20f);
            if (card.crumpled && card.ball != null) card.ball.transform.position = Vector3.Lerp(card.ball.transform.position, target, blend);
            else if (card.root != null)
            {
                card.root.position = Vector3.Lerp(card.root.position, target, blend);
                card.root.rotation = Quaternion.Slerp(card.root.rotation, SpeechBubbleDock.FacingAt(card.root.position), 1f - Mathf.Exp(-Time.deltaTime * 10f));
                card.root.localScale = Vector3.Lerp(card.root.localScale, Vector3.one, 1f - Mathf.Exp(-Time.deltaTime * 8f));
            }
            cardPath.Add((card.Position, Time.time));
            if (cardPath.Count > 8) cardPath.RemoveAt(0);
            if (!card.crumpled)
            {
                bool over = CardGoesIn(card.Position); // F79: near Maker
                if (over && !cardOverRing) ToySounds.Play(ToySounds.Cue.Pop, card.Position, .3f, 1.8f);
                cardOverRing = over;
                CrumpleGesture(card);
            }
        }

        private Vector3 CardVelocity()
        {
            if (cardPath.Count < 2) return Vector3.zero;
            var a = cardPath[0];
            var b = cardPath[cardPath.Count - 1];
            return b.time - a.time > .01f ? (b.at - a.at) / (b.time - a.time) : Vector3.zero;
        }

        private void DropCard(Vector3? thrown = null)
        {
            var card = carriedCard;
            if (card == null) return;
            Vector3 velocity = thrown ?? CardVelocity();
            carriedCard = null;
            cardHand = null;
            cardTarget = null;
            cardMouse = false;
            cardFar = -1f;
            cardOverRing = false;
            fistSince = -1f;
            Vector3 at = card.Position;
            if (card.crumpled) { ReleaseCrumpled(card, at, velocity); return; }
            card.peeled = false;
            if (CardGoesIn(at)) { KeepCard(card); return; } // F79: given to Maker
            // Anywhere else it stays where it was let go (not used until it is on the note).
            card.home = at;
            ToySounds.Play(ToySounds.Cue.Paper, at, .35f, 1f);
        }

        // ------------------------------------------------------------ on the note

        private void KeepCard(ShapeCard card)
        {
            int same = KeptCards.Count(c => c.kind == card.kind);
            if ((card.kind == CardKind.Photo && same >= MaxPhotos) || (card.kind == CardKind.Drawing && same >= MaxDrawings))
            {
                Say(Crew.Maker, card.kind == CardKind.Photo ? "Three photos is the most. Take one off first." : "Two drawings is the most. Take one off first.");
                if (RingPose(out var at, out var facing, out _, out float radius)) card.home = CardRest(at + facing * Vector3.right * (radius + .14f) + Vector3.up * .06f);
                ToySounds.Play(ToySounds.Cue.Bonk, card.Position, .3f, 1.3f);
                return;
            }
            card.kept = true;
            card.peeled = false;
            card.hopAt = Time.time;
            keepsThisStep++;
            summaryToken++;
            UpdateShapeWords();
            ToySounds.Play(ToySounds.Cue.Press, card.Position, .5f, 1.5f);
            if (GiveCardsToMaker)
            {
                // F79: Maker takes it (a nod, a puff in its hands); the card joins the fan beside it.
                ToySounds.Play(ToySounds.Cue.Paper, card.Position, .5f, 1.3f);
                if (MakerTakes(out var hands)) ToyFx.Puff(hands, .008f, 3);
                Body(Crew.Maker)?.Play("Idea_Pop");
                Talk.Add("The person gave Maker a " + CardKindName(card) + " card" + (string.IsNullOrEmpty(card.role) ? "." : " (" + card.role + ")."));
                Heard();
                if (AskPolishOnGive(card)) return; // F86: a drawing: polish it or keep it, then the role
                if (string.IsNullOrEmpty(card.role)) AskRole(card);
                else SayDoneLine();
                return;
            }
            // (The ring keeps its size: growing it pushed the crew off their seats with every card. It is inked round
            // once more instead, as when the note itself settled in it.)
            arrival.RingNoteIn();
            if (RingPose(out _, out var noteTurn, out var centre, out _))
                ToyFx.Ring(NoteOnWall ? centre - noteTurn * Vector3.forward * .01f : centre + Vector3.up * .004f, NoteOnWall ? noteTurn : Quaternion.Euler(90f, 0f, 0f), .08f); // F72
            Talk.Add("The person put a " + CardKindName(card) + " card on the note" + (string.IsNullOrEmpty(card.role) ? "." : " (" + card.role + ")."));
            Heard();
            ShowShapeDone();
            if (string.IsNullOrEmpty(card.role)) AskRole(card);
            else SayDoneLine();
        }

        // F79: "Anything else, or is that all?" after each card Maker gets (and its role is known).
        public int MoreAsked { get; private set; }
        public int MoreGiven { get; private set; }
        private void AskForMore()
        {
            if (Current != Step.ShapeInput || summarising) return;
            MoreAsked++;
            AskNow(Crew.Maker, AnythingElseLine, false, Rp(AddMoreLabel, "add more"), Rp(ThatsAllLabel, "that's all")); // (at once: Calm.cs AskNow)
        }

        private static string CardKindName(ShapeCard card) => card.kind == CardKind.Words ? "words" : card.kind == CardKind.Photo ? "photo" : "drawing";

        // Where the i-th card on the note stands: beside the note, left and right in turn, in rows on the desk
        // (x to the person's right, y toward the person, in metres from the ring's middle; z a small turn). Never
        // behind the note: that is where the crew sits.
        private static readonly float[] KeptRows = { .035f, -.04f, .11f };
        private static Vector3 KeptSlot(int i)
        {
            float side = i % 2 == 0 ? -1f : 1f;
            int k = i / 2, column = k / KeptRows.Length, row = k % KeptRows.Length;
            return new Vector3(side * (.108f + column * .08f), KeptRows[row], side * -5f);
        }

        // The first card on the note: Maker says how the collecting ends (once per shape step).
        private void SayDoneLine()
        {
            if (GiveCardsToMaker) { AskForMore(); return; } // F79: asked after every card
            if (doneLineSaid) return;
            doneLineSaid = true;
            Say(Crew.Maker, "Add more if you like. Say done when that's all.");
            PulseShapeDone();
        }

        // ------------------------------------------------------------ what a card is for

        private void AskRole(ShapeCard card)
        {
            if (roleCard != null && roleCard != card) DropRoleQuestion();
            roleCard = card;
            card.asked = true;
            string ask = card.kind == CardKind.Words ? "Are these words about the whole shape, or one part? Tell me which."
                : card.kind == CardKind.Photo ? "Is this picture the whole shape, or one part? Tell me which."
                : "Is your drawing the whole shape, or one part? Tell me which.";
            // 2026-11-15: asked the moment the card is given (it waited behind "Give it to me!" being read).
            AskNow(CardOwner(card), ask, false, Rp(WholeShape), Rp("Just a detail"));
        }

        private void DropRoleQuestion()
        {
            roleCard = null;
            if (Question != null && !Question.answered && !Question.toCrew && Question.text.Contains("whole shape, or one part")) CloseQuestion("the card moved on");
        }

        private static readonly (string word, string role)[] PartWords =
        {
            ("handle", "Handle"), ("lid", "Lid"), ("base", "Base"), ("bottom", "Base"), ("foot", "Foot"), ("feet", "Feet"), ("legs", "Legs"), ("leg", "Leg"),
            ("top view", "Top view"), ("from above", "Top view"), ("side view", "Side view"), ("from the side", "Side view"), ("front view", "Front view"),
            ("rim", "Rim"), ("spout", "Spout"), ("body", "Body"), ("opening", "Opening"), ("neck", "Neck"), ("back", "Back"), ("front", "Front"), ("side", "Side"), ("top", "Top"),
            ("손잡이", "Handle"), ("뚜껑", "Lid"), ("바닥", "Base"), ("밑", "Base"), ("받침", "Base"), ("다리", "Legs"), ("위에서", "Top view"), ("옆에서", "Side view"), ("앞에서", "Front view"),
            ("몸통", "Body"), ("본체", "Body"), ("입구", "Opening"), ("주둥이", "Spout"), ("테두리", "Rim"), ("윗", "Top"), ("위", "Top"), ("옆", "Side"), ("앞", "Front"), ("뒤", "Back")
        };

        private static bool WholeWords(string p) =>
            new[] { "whole", "entire", "all of it", "overall", "everything", "the shape itself", "전체", "전부", "통째", "모양 그대로", "다" }.Any(w => w.Length > 1 ? p.Contains(w) : p == w);
        private static bool DetailWords(string p) =>
            new[] { "detail", "decoration", "pattern", "motif", "ornament", "accent", "장식", "무늬", "디테일", "포인트", "문양" }.Any(p.Contains);

        // A part named in the words themselves ("a handle like a ring"): its role is known without asking. Only words
        // that can hardly mean the whole thing, and only when one such part is named ("round body, wide base" is
        // about the whole shape).
        private static readonly (string word, string role)[] SurePartWords =
        {
            ("handle", "Handle"), ("lid", "Lid"), ("spout", "Spout"), ("legs", "Legs"), ("feet", "Feet"), ("rim", "Rim"), ("neck", "Neck"),
            ("top view", "Top view"), ("from above", "Top view"), ("side view", "Side view"), ("from the side", "Side view"), ("front view", "Front view"),
            ("손잡이", "Handle"), ("뚜껑", "Lid"), ("주둥이", "Spout"), ("테두리", "Rim"), ("위에서", "Top view"), ("옆에서", "Side view"), ("앞에서", "Front view")
        };

        private static string RoleInWords(string p)
        {
            string found = null;
            foreach (var (word, role) in SurePartWords)
            {
                if (!(word.Any(ch => ch > 0x250) ? p.Contains(word) : Regex.IsMatch(p, @"\b" + Regex.Escape(word) + @"s?\b"))) continue;
                if (found != null && found != role) return null;
                found = role;
            }
            return found;
        }

        private static readonly HashSet<string> NotAPart = new HashSet<string>
        {
            "make", "add", "want", "like", "please", "wait", "no", "yes", "yeah", "okay", "ok", "hmm", "um", "uh", "more", "less", "bigger", "smaller", "taller", "shorter",
            "rounder", "round", "wider", "thinner", "thicker", "can", "should", "is", "are", "be", "do", "not", "and", "with", "what", "why", "how", "this", "that", "one", "two", "thanks"
        };

        // The answer to "what is it for?": the whole shape, a detail, or a part by its name.
        private static string ParseRole(string text, string p, out string raw)
        {
            raw = text;
            if (WholeWords(p)) return WholeShape;
            if (DetailWords(p)) return DetailRole;
            foreach (var (word, role) in PartWords)
                if (word.Any(ch => ch > 0x250) ? p.Contains(word) : Regex.IsMatch(p, @"\b" + Regex.Escape(word) + @"s?\b")) return role;
            if (p == "a part" || p == "one part" || p == "part" || p.Contains("부분")) return null; // which part? the question stays
            if (!Latin(p)) return "Part";
            // Any other short answer names the part ("the little hook on the side" -> "Little hook on").
            string name = Regex.Replace(p, @"^(it's|it is|this is|that's|that is|just|only|for)\s+", "");
            name = Regex.Replace(name, @"\b(the|a|an|its|my|part|of|it|for)\b", " ");
            var words = name.Split(new[] { ' ', ',' }, System.StringSplitOptions.RemoveEmptyEntries);
            // (Only a short name: "make it rounder" or "yes" is not a part.)
            if (words.Length == 0 || words.Length > 2 || string.Join(" ", words).Length > 22 || words.Any(NotAPart.Contains)) return null;
            string joined = string.Join(" ", words);
            return char.ToUpperInvariant(joined[0]) + joined.Substring(1);
        }

        // OnVoice / Route, before anything else reads the words: the open "what is it for?" question is answered.
        private bool ShapeRoleVoice(string text, string p)
        {
            if (roleCard == null || Current != Step.ShapeInput) return false;
            if (!ShapeCards.Contains(roleCard) || !roleCard.kept) { roleCard = null; return false; }
            if (DoneWords(p) || YouPick(p) || ThisWords(p) || QuestionLike(p) || WantsToDraw(p) || p.Contains("start over") || p.Contains("new creation") || p.Split(' ').Length > 8) return false;
            string role = ParseRole(text, p, out string raw);
            if (role == null) return false;
            var card = roleCard;
            roleCard = null;
            SetRole(card, role, raw);
            return true;
        }

        private void SetRole(ShapeCard card, string role, string raw)
        {
            card.role = role;
            card.roleRaw = raw ?? "";
            card.hopAt = Time.time;
            summaryToken++;
            ShowRole(card);
            UpdateShapeWords();
            if (Question != null && !Question.answered && !Question.toCrew && Question.text.Contains("whole shape, or one part")) CloseQuestion("accepted spoken answer");
            Talk.Add("The " + CardKindName(card) + " card is for: " + role + (raw != null && raw != role ? " (\"" + raw + "\")" : ""));
            // 2026-11-15: given to Maker, the answer is a nod ("Got it!" is a hop, no bubble to read: the role is on the
            // card's tab) and Maker's "Anything else?" comes at once.
            if (GiveCardsToMaker && card.kept) Ack(CardOwner(card), "Got it!");
            else Ack(CardOwner(card), role == WholeShape ? "The whole shape. Got it!" : role == DetailRole ? "A detail. Got it!" : "The " + role.ToLowerInvariant() + ". Got it!");
            if (card.kept) SayDoneLine();
        }

        // "This is the handle" about the picture or drawing just made or just put down: its role, not a new card.
        private bool RoleForRecentCard(string text, string p)
        {
            var m = Regex.Match(p, @"^(this|this one|it|that|that one)('s| is)\s+(for\s+)?(.+)$");
            string rest = m.Success ? m.Groups[4].Value : null;
            if (rest == null)
            {
                var k = Regex.Match(p, @"^(이건|이거는|이게|이거|그건|그게)\s*(.+)$");
                if (k.Success) rest = k.Groups[2].Value;
            }
            if (rest == null) return false;
            var card = carriedCard != null && carriedCard.HasImage ? carriedCard
                : ShapeCards.Where(c => c.HasImage && !c.crumpled && string.IsNullOrEmpty(c.role) && Time.time - c.born < 25f).OrderByDescending(c => c.born).FirstOrDefault();
            if (card == null) return false;
            string role = ParseRole(rest, rest, out _);
            if (role == null) return false;
            if (roleCard == card) roleCard = null;
            SetRole(card, role, text);
            return true;
        }

        // ------------------------------------------------------------ what is said in the shape step

        private bool ShapeVoice(string text, string p)
        {
            if (takingSketchbook || SketchbookHeld || paperInFlight) return true;
            if (YouPick(p))
            {
                StopTutorial();
                ShapeFromCrew = true;
                Ack(Crew.Maker, "Okay, I'll pick a shape!");
                FinishShape();
                return true;
            }
            if (ThisWords(p) && FrameShown) { TakeShowPicture(true); return true; }
            // F79: Add more (the pill or the words): Maker holds its sketchbook out again and waits for more.
            if (p == "add more" || p == "more" || p == "i have more" || p == "one more" || p.Contains("더 있") || p.Contains("더 줄"))
            {
                MoreGiven++;
                Ack(Crew.Maker, "Okay! Show me more.");
                if (!Drawing && !OfferingBook) StartOfferingBook();
                return true;
            }
            if (DoneWords(p))
            {
                if (capturingShow || Drawing) return true;
                ShapeDone("said");
                return true;
            }
            summaryToken++; // anything else said while Maker reads the cards back stops the reading
            // "Like this mug" / "like that one over there": Spark looks at what the person looks or points at.
            if (RoleForRecentCard(text, p)) return true; // ("this is the handle": the card just made, not something to look at)
            if (PointsAtSomething(p) && !ThisWords(p) && Eyes != null && !Eyes.Looking) { StartCoroutine(LookForInput(text)); return true; }
            AddWordsCard(text);
            return true;
        }

        // ------------------------------------------------------------ done

        public void ShapeDone(string how)
        {
            if (Current != Step.ShapeInput || summarising) return;
            Heard();
            if (KeptCount == 0)
            {
                bool some = ShapeCards.Any(c => !c.crumpled);
                Ack(Crew.Maker, some ? (GiveCardsToMaker ? "Give me the ones you like first!" : "Put the ones you like on your note first!") : "Tell me, draw or snap something first. Or say you pick!");
                if (some && !GiveCardsToMaker) arrival.RingNoteIn();
                return;
            }
            Talk.Add("The person is done collecting the shape (" + how + ").");
            StartCoroutine(ShapeSummary());
        }

        // Maker reads the cards on the note back (each hops as it is named), then draws.
        private IEnumerator ShapeSummary()
        {
            summarising = true;
            int token = ++summaryToken;
            DropRoleQuestion();
            var kept = KeptCards.ToList();
            string line = SummaryLine(kept);
            LastSummary = line;
            Say(Crew.Maker, line);
            if (kept.Count(c => c.role == WholeShape) >= 2) Say(Crew.Maker, "Two whole shapes, so I'll sketch both ways.");
            float until = Time.time + 8f;
            while (lines.Any(l => l.text == line) && Time.time < until)
            {
                if (token != summaryToken || Current != Step.ShapeInput) { summarising = false; yield break; }
                yield return null;
            }
            foreach (var card in kept)
            {
                if (token != summaryToken || Current != Step.ShapeInput) { summarising = false; yield break; }
                card.hopAt = Time.time;
                ToySounds.Play(ToySounds.Cue.Pop, card.Position, .3f, 1.5f);
                yield return new WaitForSeconds(Scripted ? .15f : .45f);
            }
            for (float wait = Time.time + (Scripted ? .3f : 1.4f); Time.time < wait; )
            {
                if (token != summaryToken || Current != Step.ShapeInput) { summarising = false; yield break; }
                yield return null;
            }
            summarising = false;
            if (Current == Step.ShapeInput) FinishShape();
        }

        private static string RolePhrase(ShapeCard c) =>
            string.IsNullOrEmpty(c.role) ? "" : c.role == WholeShape ? " as the whole shape" : c.role == DetailRole ? " as a detail" : " for the " + c.role.ToLowerInvariant();

        private static string SummaryLine(List<ShapeCard> kept)
        {
            var parts = kept.Select(c => (c.kind == CardKind.Words ? "\"" + Shorten(Latin(c.text) ? c.text : "your words", 26) + "\"" : c.kind == CardKind.Photo ? "your photo" : "your drawing") + RolePhrase(c)).ToList();
            return "Okay: " + string.Join(", ", parts) + ".";
        }

        // What the sketch maker is told about one card's picture (it goes along as an image).
        private static string CardWhat(ShapeCard c) => c.kind == CardKind.Drawing ? "the person's own line drawing" : "a picture the person took (a real object, a drawing on paper, or a picture on a screen)" + (string.IsNullOrEmpty(c.text) ? "" : " of " + c.text);

        // F63: the person's whole-shape picture for sketches 2 and 3: its outline stays, the form follows the prompt.
        private const string OutlineWords = "keep its outline recognisable (the same silhouette and main parts, seen the same way up as the object is used, never stood on its edge), but build the form exactly as the prompt describes: its height, profile, walls, edges and feet may clearly differ from this picture; ignore paper lines, colours and surface";
        private static string OutlineRole(ShapeCard c) => CardWhat(c) + ": the person's shape: " + OutlineWords;

        private static string CardRefRole(ShapeCard c)
        {
            string what = CardWhat(c);
            // F60: copied, not reinterpreted (a drawn lamp of a box, a post and a pointed shade came back as a hooded desk lamp).
            if (c.role == WholeShape) return what + ": copy exactly this shape for the whole object: the same parts in the same places, the same proportions and outline; do not redesign it, add nothing, ignore paper lines, colours and surface";
            if (c.role == DetailRole) return what + ": a detail or motif to put on the object (raised, cut out or as its outline accent), keeping the object's overall shape from the other inputs";
            if (!string.IsNullOrEmpty(c.role)) return what + " showing the object's " + c.role.ToLowerInvariant() + (string.IsNullOrEmpty(c.roleRaw) || c.roleRaw == c.role ? "" : " (the person said: " + c.roleRaw + ")") + ": give that part this shape";
            return what + ": use its shape for the object as fits best";
        }

        // ------------------------------------------------------------ the Done pill, by the note

        private void ShowShapeDone()
        {
            BuildShapeDone();
            if (ShapeDoneShown) return;
            shapeDone.gameObject.SetActive(true);
            shapeDonePill.localScale = Vector3.zero;
            doneShownAt = Time.time;
            donePressing = false;
            if (DonePose(out var at)) shapeDone.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at));
            ToySounds.Play(ToySounds.Cue.Ask, shapeDone.position, .6f, 1f); // F76
        }

        private void HideShapeDone()
        {
            if (shapeDone != null) shapeDone.gameObject.SetActive(false);
            donePress.Clear();
            donePressing = false;
        }

        private void PulseShapeDone() => donePulseAt = Time.time;

        private void BuildShapeDone()
        {
            if (shapeDone != null) return;
            shapeDone = new GameObject("Shape Done").transform;
            shapeDone.SetParent(transform, false);
            Color role = MakerRoles.Color(Crew.Maker), face = Color.Lerp(role, Color.white, .8f);
            shapeDonePill = Panel("Shape Done Pill", .05f, ChipHeight, face, out var text, ChipFont);
            text.text = "Done";
            text.fontStyle = FontStyles.Bold;
            MakeRaised(shapeDonePill, .05f, ChipHeight, face, role); // F77: a raised button like the reply pills
            shapeDonePill.SetParent(shapeDone, false);
            shapeDone.gameObject.SetActive(false);
        }

        // F77 (the user: "Done is right, above Maker's bubble"): where Maker's answers go, just above its speech bubble
        // (above its open pills, if it is asking something), not on the note any more.
        private readonly List<Renderer> makerBubbleParts = new List<Renderer>();
        private bool DonePose(out Vector3 at)
        {
            at = Vector3.zero;
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null) return false;
            var bubble = maker.authoredBubble;
            if (bubble != null && makerBubbleParts.Count == 0) makerBubbleParts.AddRange(bubble.GetComponentsInChildren<Renderer>(true));
            bool showing = bubble != null && bubble.gameObject.activeInHierarchy;
            Vector3 bottom = AboveBubble(maker, makerBubbleParts, showing);
            var q = Question;
            if (q != null && !q.answered && q.who == Crew.Maker && q.root != null && q.chips.Count > 0)
                bottom.y = Mathf.Max(bottom.y, PillsTop(q) + ChipGap);
            at = bottom + Vector3.up * (ChipHeight * .5f);
            at += AheadOfBubbles(at, .03f, ChipHeight * .5f + .004f); // F81: never behind a speech card
            return true;
        }

        private bool OnShapeDone(Vector3 world, float margin, float back, float front)
        {
            if (float.IsInfinity(world.x)) return false;
            Vector3 l = shapeDone.InverseTransformPoint(world);
            return Mathf.Abs(l.x) <= .025f + margin && Mathf.Abs(l.y) <= ChipHeight * .5f + margin && l.z >= back && l.z <= front;
        }

        // F67: the hand's ray on the Done pill (FarAim: a tap, before the cards beside it).
        private bool ShapeDoneOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            if (!ShapeDoneShown || donePressing || shapeDone == null || DoneDeferred) return false; // F76: one ask at a time
            if (!new Plane(shapeDone.forward, shapeDone.position).Raycast(ray, out float d) || d <= 0f || d >= 4f) return false;
            distance = d;
            return OnShapeDone(ray.GetPoint(d), .004f + d * .01f, -.01f, .01f);
        }

        private bool ShapeDonePinch(Vector3 point, Ray? ray)
        {
            if (!ShapeDoneShown || donePressing || DoneDeferred) return false; // F76
            bool hit = OnShapeDone(point, .012f, -.03f, .03f);
            if (!hit && ray.HasValue && new Plane(shapeDone.forward, shapeDone.position).Raycast(ray.Value, out float d) && d > 0f && d < 4f)
                hit = OnShapeDone(ray.Value.GetPoint(d), .004f + d * .01f, -.01f, .01f);
            if (!hit) return false;
            PressShapeDone();
            return true;
        }

        public void PressShapeDone()
        {
            if (!ShapeDoneShown || donePressing) return;
            donePressing = true;
            ShapeDonePresses++;
            PressRaised(shapeDonePill); // F77
            ToySounds.Play(ToySounds.Cue.Press, shapeDone.position, .45f, 1.7f);
            ToyFx.Puff(shapeDone.position, .004f, 2);
            StartCoroutine(After(.2f, () => { donePressing = false; ShapeDone("button"); }));
        }

        // ------------------------------------------------------------ every frame

        private void ShapeCardsFrame()
        {
            if (ShapeCards.Count == 0 && !ShapeDoneShown && (!TrashShown || NoteOnWall)) return; // F72: no cards: WallItemsFrame keeps the basket
            // The step went on (a sketch was picked, or a new make): the cards have done their work.
            if (Current != Step.ShapeInput && Current != Step.Looks) { ClearShapeCards(); return; }
            CardCarryFrame();
            TrashFrame();
            if (!ShapeDoneShown || donePressing || shapeDonePill.localScale.x < .5f) return;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) { donePress.Forget(hand); continue; }
                bool on = HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip) && OnShapeDone(tip.position, PressMargin, PressNear, PressThrough); // F67
                if (donePress.Step(this, hand, on)) { PressLog("Done", hand); PressShapeDone(); return; }
            }
        }

        private void ShapeCardsLate(float blend)
        {
            if (ShapeCards.Count == 0 && !ShapeDoneShown) return;
            bool ring = RingPose(out _, out var facing, out var ringCentre, out _);
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - ringCentre, Vector3.up) : Vector3.back;
            toUser = toUser.sqrMagnitude > 1e-5f ? toUser.normalized : Vector3.back;
            Vector3 toRight = Vector3.Cross(toUser, Vector3.up); // the person's right
            float lean = Mathf.Abs((facing * Vector3.up).y);
            int slot = 0;
            WindupCharacter makerChar = null;
            bool give = GiveCardsToMaker && cast.TryGetValue(Crew.Maker, out makerChar) && makerChar != null;
            Vector3 fanSide = Vector3.zero;
            if (give) fanSide = FanSide(makerChar);
            foreach (var card in ShapeCards)
            {
                if (card.crumpled || card.root == null) continue;
                bool kept = card.kept && (give || ring);
                int mine = kept ? slot++ : 0;
                if (card == carriedCard) continue;
                float hop = Time.time - card.hopAt;
                Vector3 lift = hop < .45f ? Vector3.up * Mathf.Sin(hop / .45f * Mathf.PI) * .022f : Vector3.zero;
                Vector3 target;
                Quaternion turn;
                if (kept && give)
                {
                    // F79: a small fan standing on the desk beside Maker, four to a row.
                    Vector3 feet = makerChar.transform.position;
                    Vector3 toEyes = Head != null ? Vector3.ProjectOnPlane(Head.position - feet, Vector3.up) : Vector3.back;
                    toEyes = toEyes.sqrMagnitude > 1e-5f ? toEyes.normalized : Vector3.back;
                    float half = (card.height + .012f) * GivenScale * .5f;
                    target = feet + fanSide * (.075f + .048f * (mine % 4)) - toEyes * (.035f * (mine / 4)) + toEyes * .01f + Vector3.up * (half + .003f) + lift;
                    turn = SpeechBubbleDock.FacingAt(target) * Quaternion.Euler(0f, 0f, (mine % 2 == 0 ? -4f : 4f));
                }
                else if (kept && NoteOnWall)
                {
                    // F72: on the board, in rows under the note.
                    WallCardSlot(mine, out target, out turn);
                    target += lift;
                }
                else if (kept)
                {
                    // Standing on the desk beside the note, leaning back toward the eyes as the note does.
                    Vector3 o = KeptSlot(mine);
                    float half = (card.height + .012f) * KeptScale * .5f;
                    target = ringCentre + toRight * o.x + toUser * o.y + Vector3.up * (arrival.RingRest + half * lean + .004f) + lift;
                    turn = facing * Quaternion.Euler(0f, 0f, o.z);
                }
                else
                {
                    target = card.home + Vector3.up * Mathf.Sin(Time.time * 2f + card.id * 1.7f) * .003f + lift;
                    turn = SpeechBubbleDock.FacingAt(target);
                }
                card.root.position = Vector3.Lerp(card.root.position, target, blend);
                card.root.rotation = Quaternion.Slerp(card.root.rotation, turn, blend);
                card.root.localScale = Vector3.Lerp(card.root.localScale, Vector3.one * (kept ? (give ? GivenScale : KeptScale * (NoteOnWall ? BoardScale : 1f)) : 1f), blend);
            }
            if (!ShapeDoneShown) return;
            if (Current != Step.ShapeInput) { HideShapeDone(); return; }
            if (DonePose(out var done))
            {
                shapeDone.position = Vector3.Lerp(shapeDone.position, done, blend);
                shapeDone.rotation = SpeechBubbleDock.FacingAt(shapeDone.position);
            }
            if (donePressing) { shapeDonePill.localScale = Vector3.one * 1.15f; return; }
            // F76: a question is up: the Done pill steps aside (one ask at a time) and comes back after it.
            if (DoneDeferred) { shapeDonePill.localScale = Vector3.MoveTowards(shapeDonePill.localScale, Vector3.zero, Time.deltaTime * 6f); doneShownAt = Time.time; return; }
            float grow = Mathf.Clamp01((Time.time - doneShownAt) / .22f);
            float since = Time.time - donePulseAt;
            float bounce = since < .8f ? 1f + .18f * Mathf.Sin(since / .8f * Mathf.PI * 2f) * (1f - since / .8f) : 1f;
            shapeDonePill.localScale = Vector3.one * Mathf.SmoothStep(0f, 1f, grow) * bounce;
        }

        // Cards left lying about (never put on the note) go when the collecting is over.
        private void DropLooseCards()
        {
            if (carriedCard != null) { carriedCard = null; cardHand = null; cardTarget = null; cardMouse = false; }
            foreach (var card in ShapeCards.Where(c => !c.kept || c.crumpled).ToList())
            {
                ToyFx.Puff(card.Position, .006f, 3);
                DestroyCard(card);
            }
            HideTrash();
        }

        private void DestroyCard(ShapeCard card)
        {
            ShapeCards.Remove(card);
            if (roleCard == card) DropRoleQuestion();
            if (carriedCard == card) { carriedCard = null; cardHand = null; cardTarget = null; cardMouse = false; }
            if (card.root != null) Destroy(card.root.gameObject);
            if (card.ball != null) Destroy(card.ball.gameObject);
            if (card.texture != null) Destroy(card.texture);
            card.root = null;
            card.ball = null;
        }

        private void ClearShapeCards()
        {
            foreach (var card in ShapeCards.ToList()) DestroyCard(card);
            carriedCard = roleCard = null;
            cardHand = null;
            cardTarget = null;
            summarising = false;
            summaryToken++;
            doneLineSaid = false;
            autoDoneAt = -1f;
            keepsThisStep = 0;
            CardCoaches = 0;
            HideShapeDone();
            HideTrash();
        }

        // ------------------------------------------------------------ tests

        public ShapeCard SimulateWordsCard(string said) => Current == Step.ShapeInput ? AddWordsCard(said) : null;
        public bool SimulatePickCard(ShapeCard card)
        {
            if (card == null || !ShapeCards.Contains(card) || carriedCard != null) return false;
            cardTarget = card.Position;
            CarryCard(card, null, Vector3.positiveInfinity, -1f, null);
            return true;
        }
        public void SimulateCarryCard(Vector3 to) { if (carriedCard != null) { cardTarget = to; CardCarryFrame(); } }
        public void SimulateReleaseCard(Vector3? thrown = null) { if (carriedCard != null) DropCard(thrown); }
        public Vector3 NotePoint => RingPose(out var at, out _, out _, out _) ? at : Vector3.zero;
    }
}
