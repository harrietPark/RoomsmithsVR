using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;
using Windups.BubbleLab;

namespace Windups.Handoff
{
    // Free-standing cards, piles, swipe browsing, hand gestures and the team "thinking" pill.
    //  - A character card released in empty space stays exactly there: a copy of the card
    //    (a full TransferableSpeechBubble, so it can be picked up again) is left at the release
    //    pose and the character's own card pops back home with its next line.
    //  - Cards released on top of another placed card join it as a pile (a small deck).
    //  - Microgesture thumb swipes (or Editor arrow keys) flip through the focused pile, or
    //    through the earlier lines stacked behind a character's card.
    //  - While holding a card: close the hand into a fist to crumple it; pinch it with the other
    //    hand and pull the hands apart to expand it (push them together to crumple).
    public sealed partial class BubbleInteractions
    {
        private sealed class Pile
        {
            public readonly List<CardState> cards = new List<CardState>(); // front first
            public Vector3 position;
            public Quaternion rotation;
        }

        public const float ExpandScale = 1.6f, PileRadius = .07f;
        private readonly List<Pile> piles = new List<Pile>();
        private readonly Dictionary<WindupCharacter, int> browse = new Dictionary<WindupCharacter, int>();
        private readonly Dictionary<WindupCharacter, float> browseUntil = new Dictionary<WindupCharacter, float>();
        private object swipeFocus;
        private float swipeFocusUntil, lastSwipe;
        private OVRHand[] ovrHands;
        private Transform teamThinking;
        private readonly Transform[] teamDots = new Transform[3];

        public int FreeCardCount => cards.FindAll(c => c.free).Count;
        public int PileCount => piles.Count;
        public IReadOnlyList<TransferableSpeechBubble> PileCards(int index)
        {
            var list = new List<TransferableSpeechBubble>();
            if (index >= 0 && index < piles.Count) foreach (var s in piles[index].cards) list.Add(s.card);
            return list;
        }
        public int BrowseIndex(WindupCharacter actor) => browse.TryGetValue(actor, out int k) ? k : 0;
        public bool IsExpanded(TransferableSpeechBubble card) => cards.Find(c => c.card == card)?.expanded ?? false;
        public bool TeamThinkingVisible => teamThinking != null && teamThinking.gameObject.activeSelf;

        // ------------------------------------------------------------ free cards and piles

        // The live card leaves a copy exactly where it was released; the character keeps going.
        private CardState LeaveFreeCard(CardState live)
        {
            var t = live.card.transform;
            var state = CreateFreeCard(live, live.text, live.summary, live.line, live.stickers, t.position, t.rotation, true);
            state.card.transform.localScale = t.lossyScale;
            state.expanded = live.expanded;
            state.scale = live.scale;
            if (state.expanded) { state.paper.OpenFully(); if (state.look != null) state.look.Reading = true; }
            AddToPile(state, t.position, t.rotation);
            return state;
        }

        // A grabbable card copied from a character's live card (for its look) with the given line.
        private CardState CreateFreeCard(CardState template, string text, string summary, MeetingLine line, IEnumerable<string> stickers,
            Vector3 position, Quaternion rotation, bool keepCopiedStickers)
        {
            var go = Instantiate(template.card.gameObject, position, rotation);
            go.SetActive(true);
            go.transform.localScale = Vector3.one;
            var clone = go.GetComponent<TransferableSpeechBubble>();
            clone.startingDock = null; // stays where it is, never docks itself on Start
            string author = line != null && !string.IsNullOrWhiteSpace(line.speaker) ? line.speaker : template.owner.characterName;
            go.name = author + " card (placed)";
            clone.SetText(author, summary, text);
            var state = new CardState
            {
                card = clone, owner = template.owner, home = null, free = true,
                paper = go.GetComponent<TangibleBubble>(), look = go.GetComponent<SpeechBubblePresentation>(),
                line = line, text = text, summary = summary, spoken = true, baseScale = 1f
            };
            foreach (Transform child in go.transform)
                if (child.name.StartsWith("Sticker "))
                {
                    if (keepCopiedStickers) state.stuck.Add(child);
                    else Destroy(child.gameObject);
                }
            if (stickers != null)
            {
                state.stickers.AddRange(stickers);
                if (!keepCopiedStickers)
                    foreach (var kind in state.stickers)
                    {
                        var sticker = MakeSticker(kind, clone.transform, material, font, 1f);
                        sticker.localScale = Vector3.one * .00062f;
                        state.stuck.Add(sticker);
                    }
            }
            clone.Released += () => OnReleased(state);
            cards.Add(state);
            return state;
        }

        // ------------------------------------------------------------ earlier lines out of decks

        // An earlier line behind a card becomes a real card again (to grab, place, hand over).
        public TransferableSpeechBubble PromoteSnapshot(PaperSnapshot snapshot)
        {
            EnsureSetup();
            if (snapshot == null) return null;
            WindupCharacter deckOwner = null;
            foreach (var pair in decks) if (pair.Value.Contains(snapshot)) deckOwner = pair.Key;
            if (deckOwner == null) return null;
            var template = cards.Find(c => !c.free && c.owner.characterName == snapshot.Speaker) ?? cards.Find(c => !c.free && c.owner == deckOwner);
            if (template == null) return null;
            string summary = snapshot.Line != null && !string.IsNullOrWhiteSpace(snapshot.Line.summary) ? snapshot.Line.summary : snapshot.Text;
            var t = snapshot.transform;
            var state = CreateFreeCard(template, snapshot.Text, summary, snapshot.Line, snapshot.Stickers, t.position, t.rotation, false);
            decks[deckOwner].Remove(snapshot);
            browse.Remove(deckOwner);
            Destroy(snapshot.gameObject);
            ToySounds.Play(ToySounds.Cue.Paper, t.position, .6f, 1.2f);
            return state.card;
        }

        // A card that was pulled out but never picked up settles where it is.
        public void SettleFree(TransferableSpeechBubble card)
        {
            var state = cards.Find(c => c.card == card && c.free);
            if (state != null && !card.IsHeld) AddToPile(state, card.transform.position, card.transform.rotation);
        }

        public PaperSnapshot DeckSnapshotHit(Ray ray, out float distance)
        {
            distance = float.PositiveInfinity;
            if (!Active || !setup) return null;
            PaperSnapshot best = null;
            foreach (var deck in decks.Values)
                foreach (var snapshot in deck)
                    if (snapshot.gameObject.activeInHierarchy && snapshot.Raycast(ray, out float d) && d < distance) { best = snapshot; distance = d; }
            return best;
        }

        public PaperSnapshot DeckSnapshotAt(Vector3 point)
        {
            if (!Active || !setup) return null;
            foreach (var deck in decks.Values)
                foreach (var snapshot in deck)
                    if (snapshot.gameObject.activeInHierarchy && snapshot.Touches(point)) return snapshot;
            return null;
        }

        // A quick pinch-and-release (or click) on a card or a deck copy.
        public void RemoteTap(TransferableSpeechBubble card)
        {
            if (!Active) return;
            EnsureSetup();
            var state = cards.Find(c => c.card == card);
            if (state != null && Hoverable(state)) Tap(state);
        }

        public void TapSnapshot(PaperSnapshot snapshot)
        {
            if (!Active || snapshot == null) return;
            EnsureSetup();
            Activate(snapshot);
        }

        // Join the nearest pile (within a few centimetres) or start a new one at this pose.
        private void AddToPile(CardState state, Vector3 position, Quaternion rotation)
        {
            RemoveFromPile(state);
            Pile best = null;
            float bestDistance = PileRadius;
            foreach (var pile in piles)
            {
                float d = Vector3.Distance(pile.position, position);
                if (d < bestDistance) { best = pile; bestDistance = d; }
            }
            if (best == null)
            {
                best = new Pile { position = position, rotation = rotation };
                piles.Add(best);
            }
            best.cards.Insert(0, state);
            if (best.cards.Count > 1)
            {
                ToySounds.Play(ToySounds.Cue.Paper, best.position, .6f, 1.15f);
                ToyFx.Puff(best.position, .006f, 2);
            }
        }

        private void RemoveFromPile(CardState state)
        {
            for (int i = piles.Count - 1; i >= 0; i--)
            {
                piles[i].cards.Remove(state);
                if (piles[i].cards.Count == 0) piles.RemoveAt(i);
            }
        }

        private Pile PileOf(CardState state) => piles.Find(p => p.cards.Contains(state));

        private void DestroyFree(CardState state)
        {
            RemoveFromPile(state);
            cards.Remove(state);
            if (trayCard == state) trayCard = null;
            if (state.card.Owner != null) state.card.Owner.Remove(state.card);
            Destroy(state.card.gameObject);
        }

        // The front card sits at the pile's pose; the rest fan slightly back and up behind it.
        private void LayoutPiles()
        {
            foreach (var pile in piles)
            {
                Vector3 back = pile.rotation * Vector3.forward, up = pile.rotation * Vector3.up;
                for (int i = 0; i < pile.cards.Count; i++)
                {
                    var state = pile.cards[i];
                    if (state.card.IsHeld) continue;
                    bool shown = i < 4;
                    if (state.card.gameObject.activeSelf != shown) state.card.gameObject.SetActive(shown);
                    if (!shown) continue;
                    var t = state.card.transform;
                    Vector3 target = pile.position + back * (.01f * i) + up * (.012f * i);
                    float blend = 1f - Mathf.Exp(-Time.deltaTime * 14f);
                    t.SetPositionAndRotation(Vector3.Lerp(t.position, target, blend), Quaternion.Slerp(t.rotation, pile.rotation, blend));
                }
            }
        }

        // ------------------------------------------------------------ swipe browsing

        // +1 = next (older / further back), -1 = previous.
        public void Swipe(int direction)
        {
            EnsureSetup();
            var focus = SwipeTarget();
            if (focus == null) return;
            lastSwipe = Time.time;
            lastAction = Time.time;
            swipeFocus = focus;
            swipeFocusUntil = Time.time + 6f;
            if (focus is Pile pile && pile.cards.Count > 1)
            {
                if (direction > 0) { var front = pile.cards[0]; pile.cards.RemoveAt(0); pile.cards.Add(front); }
                else { var last = pile.cards[pile.cards.Count - 1]; pile.cards.RemoveAt(pile.cards.Count - 1); pile.cards.Insert(0, last); }
                ToySounds.Play(ToySounds.Cue.Paper, pile.position, .55f, direction > 0 ? 1.2f : 1.05f);
            }
            else if (focus is WindupCharacter actor)
            {
                int count = DeckCount(actor);
                if (count == 0) return;
                int k = Mathf.Clamp(BrowseIndex(actor) + direction, 0, count);
                browse[actor] = k;
                browseUntil[actor] = Time.time + 8f;
                var home = DockOf(actor);
                ToySounds.Play(ToySounds.Cue.Paper, home != null ? home.anchor.position : actor.transform.position, .55f, direction > 0 ? 1.2f : 1.05f);
            }
        }

        public void FocusSwipe(TransferableSpeechBubble card)
        {
            EnsureSetup();
            var state = cards.Find(c => c.card == card);
            if (state == null) return;
            swipeFocus = state.free ? (object)PileOf(state) : state.owner;
            swipeFocusUntil = Time.time + 6f;
        }

        // What a swipe flips: the card (pile or character deck) hovered last, else the pile or
        // character card nearest to where the user is looking.
        private WindupCharacter HeldStackOwner()
        {
            foreach (var state in cards)
                if (!state.free && state.card.IsHeld && DeckCount(state.owner) > 0) return state.owner;
            return null;
        }

        private object SwipeTarget()
        {
            // A character's card held up in the hand: flip through its earlier lines.
            foreach (var state in cards)
                if (!state.free && state.card.IsHeld && DeckCount(state.owner) > 0) return state.owner;
            if (trayCard != null) return trayCard.free ? (object)PileOf(trayCard) : trayCard.owner;
            if (swipeFocus != null && Time.time < swipeFocusUntil && !(swipeFocus is Pile p && !piles.Contains(p))) return swipeFocus;
            var head = Meeting.Head;
            if (head == null) return null;
            object best = null;
            float bestAngle = 14f;
            foreach (var pile in piles)
            {
                float angle = Vector3.Angle(head.forward, pile.position - head.position);
                if (pile.cards.Count > 1 && angle < bestAngle) { best = pile; bestAngle = angle; }
            }
            foreach (var state in cards)
            {
                if (state.free || !state.card.gameObject.activeInHierarchy || DeckCount(state.owner) == 0) continue;
                float angle = Vector3.Angle(head.forward, state.card.transform.position - head.position);
                if (angle < bestAngle) { best = state.owner; bestAngle = angle; }
            }
            return best;
        }

        // Thumb swipes along the index finger (Meta hand microgestures), either hand.
        private void UpdateMicrogestures()
        {
            if (ovrHands == null) ovrHands = FindObjectsByType<OVRHand>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (Time.time - lastSwipe < .25f) return;
            var seen = new HashSet<OVRPlugin.Hand>();
            foreach (var hand in ovrHands)
            {
                if (hand == null || !hand.isActiveAndEnabled || !seen.Add(hand.GetHand())) continue;
                var gesture = hand.GetMicrogestureType();
                if (gesture != OVRHand.MicrogestureType.NoGesture && gesture != OVRHand.MicrogestureType.Invalid)
                    Debug.Log("[Windups] microgesture " + gesture + " (" + hand.GetHand() + ")");
                if (gesture == OVRHand.MicrogestureType.SwipeLeft || gesture == OVRHand.MicrogestureType.SwipeForward) { Swipe(1); return; }
                if (gesture == OVRHand.MicrogestureType.SwipeRight || gesture == OVRHand.MicrogestureType.SwipeBackward) { Swipe(-1); return; }
                // A thumb tap also flips forward through a held card's stack.
                if (gesture == OVRHand.MicrogestureType.ThumbTap && HeldStackOwner() != null) { Swipe(1); return; }
            }
        }

        private void ExpireBrowsing()
        {
            List<WindupCharacter> done = null;
            foreach (var pair in browseUntil)
                if (Time.time > pair.Value) (done ??= new List<WindupCharacter>()).Add(pair.Key);
            if (done == null) return;
            foreach (var actor in done) { browse.Remove(actor); browseUntil.Remove(actor); }
        }

        // ------------------------------------------------------------ hold gestures

        private sealed class Stretch { public Hand other; public float start; }
        private readonly Dictionary<CardState, Stretch> stretches = new Dictionary<CardState, Stretch>();
        private readonly Dictionary<CardState, float> fists = new Dictionary<CardState, float>();

        private static bool Tip(Hand hand, out Vector3 tip)
        {
            tip = default;
            if (hand == null || !hand.isActiveAndEnabled || !hand.IsConnected) return false;
            if (!hand.GetJointPose(HandJointId.HandIndexTip, out Pose pose)) return false;
            tip = pose.position;
            return true;
        }

        // Middle, ring and little finger curled into the palm.
        private static bool IsFist(Hand hand)
        {
            if (!hand.GetJointPose(HandJointId.HandPalm, out Pose palm)) return false;
            float sum = 0f;
            foreach (var joint in new[] { HandJointId.HandMiddleTip, HandJointId.HandRingTip, HandJointId.HandPinkyTip })
            {
                if (!hand.GetJointPose(joint, out Pose tip)) return false;
                sum += Vector3.Distance(tip.position, palm.position);
            }
            return sum / 3f < .048f;
        }

        private void UpdateGestures()
        {
            if (hands == null) return;
            foreach (var state in cards)
            {
                if (!state.card.IsHeld || state.paper.IsCrumpled)
                {
                    stretches.Remove(state);
                    fists.Remove(state);
                    state.stretching = false;
                    continue;
                }
                var grab = state.card.GetComponent<Grabbable>();
                if (grab == null || grab.GrabPoints == null || grab.GrabPoints.Count == 0) continue;
                // Both hands on the sheet: BubbleLab's own transformer stretches and crumples it.
                if (grab.GrabPoints.Count >= 2)
                {
                    stretches.Remove(state);
                    state.stretching = false;
                    continue;
                }
                Vector3 grip = grab.GrabPoints[0].position;
                // The holding hand: the one whose index tip is at the grab point.
                Hand holder = null;
                float nearest = .09f;
                foreach (var hand in HandSet.Both())
                    if (Tip(hand, out var tip) && Vector3.Distance(tip, grip) < nearest) { holder = hand; nearest = Vector3.Distance(tip, grip); }
                // Fist: squeeze the held card into a ball.
                if (holder != null && IsFist(holder))
                {
                    fists.TryGetValue(state, out float since);
                    fists[state] = since + Time.deltaTime;
                    if (since + Time.deltaTime > .3f) { Crumple(state); continue; }
                }
                else fists.Remove(state);
                // Second hand pinching the card: pull apart to expand, push together to crumple.
                if (!stretches.TryGetValue(state, out var stretch))
                {
                    foreach (var hand in HandSet.Both())
                    {
                        // Tip() first: it skips inactive or untracked hands, which must not be queried.
                        if (hand == holder || !Tip(hand, out var tip) || !hand.GetIndexFingerIsPinching()) continue;
                        if (CardDistance(state, tip) > .05f || Vector3.Distance(tip, grip) < .03f) continue;
                        stretches[state] = stretch = new Stretch { other = hand, start = Mathf.Max(.03f, Vector3.Distance(tip, grip)) };
                        state.stretching = true;
                        break;
                    }
                    if (stretch == null) continue;
                }
                if (!Tip(stretch.other, out var other) || !stretch.other.GetIndexFingerIsPinching())
                {
                    stretches.Remove(state);
                    state.stretching = false;
                    continue;
                }
                float distance = Vector3.Distance(other, grip);
                if (distance < .03f && stretch.start > .045f) { Crumple(state); continue; }
                state.stretchScale = Mathf.Clamp(1f + (distance - stretch.start) * 4f, .8f, 2f);
                if (distance - stretch.start > .05f && !state.expanded) SetExpanded(state, true);
                else if (distance - stretch.start < -.02f && state.expanded) SetExpanded(state, false);
            }
        }

        private void Crumple(CardState state)
        {
            if (state.paper.IsCrumpled) return;
            state.paper.BeginTwoHands(.24f);
            state.paper.MoveTwoHands(.07f);
            stretches.Remove(state);
            fists.Remove(state);
            state.stretching = false;
            if (state.expanded) SetExpanded(state, false);
            ToySounds.Play(ToySounds.Cue.Paper, state.card.transform.position, .8f, .75f);
            ToyFx.Puff(state.card.transform.position, .008f, 3);
        }

        public void CrumpleCard(TransferableSpeechBubble card) { EnsureSetup(); var s = cards.Find(c => c.card == card); if (s != null) Crumple(s); }
        public void ExpandCard(TransferableSpeechBubble card, bool on) { EnsureSetup(); var s = cards.Find(c => c.card == card); if (s != null) SetExpanded(s, on); }

        // Expanded: the whole sentence, larger, until folded again (a tap or pushing the hands back).
        private void SetExpanded(CardState state, bool on)
        {
            if (state.expanded == on) return;
            state.expanded = on;
            if (state.look != null) state.look.Reading = on || state.reading;
            if (on) state.paper.OpenFully();
            else if (!state.reading)
            {
                if (!state.free && state.owner.IsPresenting) state.paper.OpenFully();
                else state.paper.CloseToCompact();
            }
            ToySounds.Play(ToySounds.Cue.Paper, state.card.transform.position, .55f, on ? 1.25f : .9f);
        }

        // ------------------------------------------------------------ the stack that comes with a held card

        // Under a held card that has earlier lines: "2 / 4 · swipe thumb ‹ ›" so the stack and the
        // microgesture are discoverable. Poking the faded cards behind also flips (fallback).
        private TMPro.TextMeshProUGUI stackHint;
        private int stackHintFrame = -1;
        public string StackHintText => stackHint != null && stackHint.gameObject.activeInHierarchy ? stackHint.text : "";

        private void ShowStackHint(CardState state, int count, int browsed)
        {
            if (stackHint == null)
            {
                var canvas = new GameObject("Stack Hint", typeof(RectTransform), typeof(Canvas));
                var rect = (RectTransform)canvas.transform;
                rect.SetParent(transform, false);
                rect.sizeDelta = new Vector2(260f, 30f);
                rect.localScale = Vector3.one * .001f;
                canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                stackHint = new GameObject("Text", typeof(RectTransform)).AddComponent<TMPro.TextMeshProUGUI>();
                stackHint.rectTransform.SetParent(rect, false);
                stackHint.rectTransform.anchorMin = Vector2.zero;
                stackHint.rectTransform.anchorMax = Vector2.one;
                stackHint.rectTransform.offsetMin = stackHint.rectTransform.offsetMax = Vector2.zero;
                if (font != null) stackHint.font = font;
                stackHint.fontSize = 17f;
                stackHint.color = new Color(1f, 1f, 1f, .92f);
                stackHint.alignment = TMPro.TextAlignmentOptions.Center;
                stackHint.raycastTarget = false;
            }
            stackHintFrame = Time.frameCount;
            var t = state.card.transform;
            if (!stackHint.transform.parent.gameObject.activeSelf) stackHint.transform.parent.gameObject.SetActive(true);
            float half = state.paper.Height * t.lossyScale.y * .5f;
            stackHint.transform.parent.SetPositionAndRotation(t.position - t.up * (half + .025f) - t.forward * .01f, t.rotation);
            stackHint.text = (browsed + 1) + " / " + (count + 1) + "   ·   swipe thumb  ‹  ›";
        }

        private void HideStaleStackHint()
        {
            if (stackHint != null && stackHintFrame != Time.frameCount && stackHint.transform.parent.gameObject.activeSelf)
                stackHint.transform.parent.gameObject.SetActive(false);
        }

        // Fallback to the microgesture: poke the faded cards behind the held card to flip.
        private float stackPokeAt;
        private void PokeHeldStack(List<Vector3> tips)
        {
            if (Time.time - stackPokeAt < .45f) return;
            foreach (var pair in decks)
                foreach (var copy in pair.Value)
                    if (copy.Faded && copy.gameObject.activeInHierarchy)
                        foreach (var tip in tips)
                            if (copy.Touches(tip)) { stackPokeAt = Time.time; Swipe(1); return; }
        }

        // ------------------------------------------------------------ crumpled paper and the basket

        // While a crumpled card is in the hand, Benefit (who keeps the basket) calls for it and
        // the basket lights up. Release near the basket or throw it that way and it lands in;
        // otherwise the crumpled paper stays where it fell, to be picked up again.
        public const float BasketReach = .2f, ThrowSpeed = .6f, ThrowAngle = 35f;
        private PaperSnapshot callout;
        private Transform basketGlow;
        private readonly HashSet<PaperSnapshot> flyingBalls = new HashSet<PaperSnapshot>();
        private Vector3? releaseVelocityOverride;
        public bool CalloutVisible => callout != null && callout.gameObject.activeSelf;
        public bool BasketGlowing => basketGlow != null && basketGlow.gameObject.activeSelf;
        public Vector3 BasketPosition => basket != null ? basket.position : Vector3.zero;

        private void BuildCrumpleCall()
        {
            var benefit = Actor("Benefit");
            if (basket == null || benefit == null) return;
            callout = PaperSnapshot.Create(transform, "Benefit", "Throw it to me!", null, SpeechBubblePresentation.ResolveRoleColor("Benefit"),
                material, font, .17f, .07f);
            callout.gameObject.SetActive(false);
            var ring = ToyMeshes.Torus(.12f, "BasketGlow");
            var colors = new Color[ring.vertexCount];
            for (int i = 0; i < colors.Length; i++) colors[i] = new Color(1f, .86f, .3f);
            ring.colors = colors;
            basketGlow = MeshObject("Basket Glow", ring, basket);
            basketGlow.localRotation = Quaternion.Euler(90f, 0f, 0f);
            basketGlow.gameObject.SetActive(false);
        }

        private void UpdateCrumpleCall()
        {
            if (callout == null) BuildCrumpleCall();
            if (callout == null) return;
            bool calling = false;
            foreach (var state in cards) calling |= state.card.IsHeld && state.paper.IsCrumpled;
            if (callout.gameObject.activeSelf != calling)
            {
                callout.gameObject.SetActive(calling);
                basketGlow.gameObject.SetActive(calling);
                if (calling)
                {
                    var benefit = Actor("Benefit");
                    benefit.SetCue("encourage");
                    ToySounds.Play(ToySounds.Cue.Pop, basket.position, .7f, 1.15f);
                }
            }
            if (!calling) return;
            var home = DockOf(Actor("Benefit"));
            Vector3 at = home != null ? home.anchor.position + Vector3.up * (home.lift + .1f) : basket.position + Vector3.up * .2f;
            callout.SetPose(at, SpeechBubbleDock.FacingAt(at), 1f, true, true);
            float pulse = 1f + Mathf.Sin(Time.time * 7f) * .08f;
            basketGlow.localPosition = Vector3.up * .044f;
            basketGlow.localScale = Vector3.one * (.05f * pulse);
        }

        private void ReleaseCrumpled(CardState state)
        {
            Vector3 at = state.heldAt;
            Vector3 v = releaseVelocityOverride ?? state.velocity;
            releaseVelocityOverride = null;
            bool inBasket = false;
            if (basket != null)
            {
                Vector3 toBasket = basket.position + Vector3.up * .03f - at;
                bool near = toBasket.magnitude < BasketReach;
                Vector3 flatV = Vector3.ProjectOnPlane(v, Vector3.up), flatTo = Vector3.ProjectOnPlane(toBasket, Vector3.up);
                bool thrown = v.magnitude > ThrowSpeed && flatV.sqrMagnitude > .0001f && Vector3.Angle(flatV, flatTo) < ThrowAngle;
                inBasket = near || thrown;
            }
            if (inBasket) { Discard(state, true); return; }
            // Missed: it stays where it fell, still crumpled.
            ToySounds.Play(ToySounds.Cue.Paper, at, .5f, .8f);
            if (state.free) { RemoveFromPile(state); return; }
            if (string.IsNullOrWhiteSpace(state.text)) { state.paper.OpenFully(); state.paper.CloseToCompact(); ReturnHome(state); return; }
            var t = state.card.transform;
            var copy = CreateFreeCard(state, state.text, state.summary, state.line, state.stickers, t.position, t.rotation, true);
            copy.paper.BeginTwoHands(.24f);
            copy.paper.MoveTwoHands(.07f);
            state.paper.OpenFully();
            state.paper.CloseToCompact();
            RemoveLine(state);
            ReturnHome(state);
        }

        // Test hook: the velocity the next crumpled release uses (a throw).
        public void ThrowNextRelease(Vector3 velocity) => releaseVelocityOverride = velocity;

        private System.Collections.IEnumerator FlyIntoBasket(PaperSnapshot ball, Vector3 from)
        {
            flyingBalls.Add(ball);
            Vector3 to = basket.position + Vector3.up * .035f;
            float height = Mathf.Max(.08f, Vector3.Distance(from, to) * .35f);
            // Benefit holds the basket up to catch it: the ball lands on BasketIn's frame 20.
            float flight = .45f;
            if (benefitBody != null) { benefitBody.Play("Benefit_BasketIn"); flight = BasketCatchFrame / 30f; }
            for (float t = 0f; t < 1f && ball != null; t += Time.deltaTime / flight)
            {
                if (benefitBody != null) to = basket.position + Vector3.up * .035f;
                Vector3 p = Vector3.Lerp(from, to, t) + Vector3.up * (Mathf.Sin(t * Mathf.PI) * height);
                ball.SetPose(p, Quaternion.Euler(t * 540f, t * 300f, 0f), 1f, false, true);
                yield return null;
            }
            flyingBalls.Remove(ball);
            if (basket != null)
            {
                basketShakeAt = Time.time;
                ToyFx.Puff(to + Vector3.up * .02f, .01f, 4);
                ToySounds.Play(ToySounds.Cue.Paper, to, .8f, .8f);
            }
        }

        // ------------------------------------------------------------ team thinking

        // While the team prepares a turn nobody in particular was asked, one small "•••" pill
        // floats above the ring instead of six.
        private void BuildTeamThinking()
        {
            teamThinking = new GameObject("Team Thinking").transform;
            teamThinking.SetParent(transform, false);
            var pill = MeshObject("Pill", ToyMeshes.RoundedSlab(.056f, .028f, .014f, .006f, Color.white, new Color(.88f, .88f, .92f),
                new Color(.95f, .95f, .97f), "TeamThinkingPill"), teamThinking);
            pill.localPosition = Vector3.zero;
            for (int i = 0; i < 3; i++)
            {
                teamDots[i] = MeshObject("Dot " + i, ToyMeshes.Disc(.0042f, .002f, new Color(.6f, .6f, .66f), new Color(.5f, .5f, .56f), "TeamThinkingDot"), teamThinking);
                teamDots[i].localPosition = new Vector3((i - 1) * .013f, 0f, -.0012f);
            }
            teamThinking.gameObject.SetActive(false);
        }

        private void UpdateTeamThinking()
        {
            if (teamThinking == null) return;
            bool someoneThinks = false;
            foreach (var dock in arrival.handoff.docks) someoneThinks |= dock.character.Cue == "thinking";
            bool show = Meeting.Busy && !someoneThinks && arrival.TopicRing != null;
            if (teamThinking.gameObject.activeSelf != show) teamThinking.gameObject.SetActive(show);
            if (!show) return;
            Vector3 c = arrival.TopicRing.transform.position;
            teamThinking.SetPositionAndRotation(c + Vector3.up * .17f, SpeechBubbleDock.FacingAt(c + Vector3.up * .17f));
            for (int i = 0; i < 3; i++)
            {
                float bounce = Mathf.Max(0f, Mathf.Sin(Time.time * 7f - i * .9f));
                teamDots[i].localPosition = new Vector3((i - 1) * .013f, bounce * bounce * .004f, -.0012f);
            }
        }
    }
}
