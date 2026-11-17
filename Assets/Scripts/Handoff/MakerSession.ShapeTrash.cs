using System.Collections;
using System.Linq;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F56 (2026-11-13, the user: "if I don't like it I take it off again; then a bin appears beside the black one
    // and I crumple it and throw it away"): a shape card the person does not want.
    //  - A card taken off the note, or any card crumpled, brings a small basket up beside Fixer (on a free spot of
    //    Fixer's own surface); Fixer looks at the card and, the first time, says what the basket is for.
    //  - Crumpling is F23's gesture: the holding hand closes into a fist for a moment, or the other hand pinches the
    //    card too (the Editor: X while dragging it). The card becomes a paper ball (F37's PaperBall).
    //  - The ball let go near the basket, or thrown its way, goes in: "tok", Fixer nods. A miss stays where it
    //    falls and can be picked up again. A card that is only put down stays a card.
    //  - The basket goes a few seconds after the last unwanted card.
    public sealed partial class MakerSession
    {
        public const float TrashReach = .2f, TrashThrowSpeed = .6f, TrashThrowAngle = 35f, TrashLinger = 4f;
        public bool TrashShown => trashBasket != null && trashBasket.gameObject.activeSelf;
        public Vector3 TrashPoint => trashBasket != null ? trashBasket.position + Vector3.up * .045f : Vector3.zero;
        public Transform TrashView => trashBasket;
        public int Trashed { get; private set; }
        public int Crumpled { get; private set; }

        private Transform trashBasket, trashGlow;
        private float trashUntil, fistSince = -1f;
        private bool trashToldOnce;

        private bool TrashWanted =>
            (carriedCard != null && (carriedCard.peeled || carriedCard.crumpled)) || ShapeCards.Any(c => c.crumpled && c.ball != null && !c.ball.Resting) || PinTrashWanted; // F72: a picture's ball too

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

        // While a card is carried: the holding hand makes a fist, or the other hand squeezes it too.
        private void CrumpleGesture(ShapeCard card)
        {
            if (card.crumpled || cardHand == null) return;
            if (HandFist(cardHand))
            {
                if (fistSince < 0f) fistSince = Time.time;
                if (Time.time - fistSince > .3f) { Crumple(card); return; }
            }
            else fistSince = -1f;
            foreach (var other in HandSet.Both())
            {
                if (other == null || other == cardHand || !other.isActiveAndEnabled || !HandIO.Connected(other) || !HandIO.Pinching(other)) continue;
                if (Vector3.Distance(PinchPoint(other), card.Position) < .06f) { Crumple(card); return; }
            }
        }

        private void Crumple(ShapeCard card)
        {
            if (card == null || card.crumpled || card.root == null) return;
            card.crumpled = true;
            card.kept = false;
            Crumpled++;
            fistSince = -1f;
            summaryToken++;
            if (roleCard == card) DropRoleQuestion();
            card.ball = PaperBall.Make(transform, material, card.id);
            card.ball.transform.localScale = Vector3.one * PaperBall.Radius * 1.9f;
            card.ball.transform.position = card.root.position;
            card.ball.Hold();
            card.root.gameObject.SetActive(false);
            UpdateShapeWords();
            if (KeptCount == 0) HideShapeDone();
            ToySounds.Play(ToySounds.Cue.Paper, card.ball.transform.position, .7f, .8f);
            ToyFx.Puff(card.ball.transform.position, .008f, 4);
            Talk.Add("The person crumpled a " + CardKindName(card) + " card.");
        }

        // The ball let go: into the basket (near it, or thrown at it), else it falls where it is.
        private void ReleaseCrumpled(ShapeCard card, Vector3 at, Vector3 velocity)
        {
            if (card.ball == null) return;
            bool into = false;
            if (TrashShown)
            {
                Vector3 to = TrashPoint - at;
                Vector3 flatV = Vector3.ProjectOnPlane(velocity, Vector3.up), flatTo = Vector3.ProjectOnPlane(to, Vector3.up);
                bool near = to.magnitude < TrashReach;
                bool thrown = velocity.magnitude > TrashThrowSpeed && flatV.sqrMagnitude > 1e-4f && Vector3.Angle(flatV, flatTo) < TrashThrowAngle && to.magnitude < 2.5f;
                into = near || thrown;
            }
            if (into) { StartCoroutine(IntoBasket(card, at)); return; }
            card.ball.Throw(Vector3.ClampMagnitude(velocity, 2.5f), Random.insideUnitSphere * 6f);
            trashUntil = Time.time + 12f;
        }

        private IEnumerator IntoBasket(ShapeCard card, Vector3 from)
        {
            var ball = card.ball.transform;
            ShapeCards.Remove(card);
            if (card.jpeg != null && card.jpeg == DrawingJpeg) DrawingJpeg = null; // F82: a thrown-away drawing is not the shape any more (nor in the notebook)
            if (card.root != null) Destroy(card.root.gameObject);
            if (card.texture != null) Destroy(card.texture);
            card.root = null;
            Vector3 to = TrashPoint;
            float distance = Vector3.Distance(from, to);
            float seconds = Mathf.Clamp(distance / 1.6f, .18f, .6f);
            for (float t = 0f; t < 1f && ball != null; t += Time.deltaTime / seconds)
            {
                ball.position = Vector3.Lerp(from, to, t) + Vector3.up * Mathf.Sin(t * Mathf.PI) * Mathf.Min(.18f, distance * .35f);
                ball.Rotate(420f * Time.deltaTime, 300f * Time.deltaTime, 0f);
                yield return null;
            }
            if (ball != null) Destroy(ball.gameObject);
            card.ball = null;
            Trashed++;
            trashUntil = Time.time + TrashLinger;
            ToySounds.Play(ToySounds.Cue.Press, to, .45f, 2.1f); // "tok"
            ToyFx.Puff(to, .008f, 4);
            Body(Crew.Fixer)?.Play("React_Nod");
            Talk.Add("The person threw a " + CardKindName(card) + " card into Fixer's basket.");
        }

        private void TrashFrame()
        {
            bool wanted = TrashWanted;
            if (wanted)
            {
                trashUntil = Mathf.Max(trashUntil, Time.time + TrashLinger);
                if (!TrashShown) ShowTrash();
            }
            else if (TrashShown && Time.time > trashUntil) HideTrash();
            if (!TrashShown) return;
            bool calling = (carriedCard != null && carriedCard.crumpled) || (pinCarry != null && pinCarry.ball != null); // F72
            if (trashGlow.gameObject.activeSelf != calling) trashGlow.gameObject.SetActive(calling);
            if (calling) trashGlow.localScale = Vector3.one * (.036f * (1f + Mathf.Sin(Time.time * 7f) * .08f));
            if (carriedCard != null && cast.TryGetValue(Crew.Fixer, out var fixer) && fixer != null) fixer.lookTarget = carriedCard.crumpled && carriedCard.ball != null ? carriedCard.ball.transform : carriedCard.root;
        }

        // Beside Fixer, on its own surface, where nothing else stands.
        private void ShowTrash()
        {
            BuildTrash();
            if (!cast.TryGetValue(Crew.Fixer, out var fixer) || fixer == null) return;
            Vector3 feet = fixer.transform.position;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - feet, Vector3.up).normalized : Vector3.back;
            if (toUser.sqrMagnitude < .01f) toUser = Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, toUser);
            var busy = Occupied();
            foreach (var c in cast.Values) if (c != null && c != fixer) busy.Add((c.transform.position, .06f));
            // (Occupied() lists Spark's frame when it lies on the desk.)
            if (arrival != null && arrival.TopicRing != null && arrival.TopicRing.enabled) busy.Add((arrival.TopicRing.transform.position, arrival.RingRadius + .02f));
            if (arrival != null && !arrival.MicHeld && arrival.MicRest != Vector3.zero) busy.Add((arrival.MicRest - Vector3.up * WindupsArrival.MicBase, .05f));
            Vector3 best = feet + side * .12f;
            float bestScore = float.MinValue;
            var surface = SurfaceAt(feet + Vector3.up * .03f);
            // (Clear of Fixer's own feet: 8.5 cm away the map said Fixer stood in it, and Fixer walked off.)
            foreach (float reach in new[] { .12f, .145f, .17f })
                for (int k = 0; k < 8; k++)
                {
                    Vector3 p = feet + Quaternion.Euler(0f, k * 45f, 0f) * side * reach;
                    if (surface != null && !surface.Contains(new Vector2(p.x, p.z), .04f)) continue;
                    if (surface != null) p.y = surface.Point(new Vector2(p.x, p.z)).y;
                    float clear = busy.Count == 0 ? 1f : busy.Min(b => Mathf.Abs(b.at.y - p.y) > .2f ? 1f : Vector3.ProjectOnPlane(b.at - p, Vector3.up).magnitude - b.radius - .04f);
                    if (clear < 0f) continue;
                    // The side toward the person first (easy to reach), then where there is most room.
                    float score = Mathf.Min(clear, .1f) + Vector3.Dot((p - feet).normalized, toUser) * .03f - reach * .2f;
                    if (score > bestScore) { bestScore = score; best = p; }
                }
            trashBasket.position = best;
            trashBasket.rotation = Quaternion.identity;
            trashBasket.localScale = Vector3.one * .2f;
            trashBasket.gameObject.SetActive(true);
            StartCoroutine(Grow(trashBasket, 1f, .3f));
            ToySounds.Play(ToySounds.Cue.Pop, best, .4f, 1.1f);
            ToyFx.Puff(best + Vector3.up * .02f, .008f, 4);
            Body(Crew.Fixer)?.Play("HandRaise");
            if (!trashToldOnce)
            {
                trashToldOnce = true;
                Say(Crew.Fixer, "Don't want it? Crumple it and toss it in here.");
            }
        }

        private void HideTrash()
        {
            if (trashBasket != null) trashBasket.gameObject.SetActive(false);
        }

        private void BuildTrash()
        {
            if (trashBasket != null) return;
            trashBasket = new GameObject("Fixer Basket").transform;
            trashBasket.SetParent(transform, false);
            Color role = MakerRoles.Color(Crew.Fixer), outside = Color.Lerp(role, Color.white, .25f), inside = Color.Lerp(role, Color.black, .25f), rim = Color.Lerp(role, Color.white, .6f);
            var mesh = ToyMeshes.Basket(.034f, .05f, .0035f, outside, inside, rim);
            owned.Add(mesh);
            var bowl = Part("Bowl", mesh);
            bowl.SetParent(trashBasket, false);
            var ring = ToyMeshes.Torus(.12f, "TrashGlow");
            Paint(ring, new Color(1f, .86f, .3f));
            owned.Add(ring);
            trashGlow = Part("Glow", ring);
            trashGlow.SetParent(trashBasket, false);
            trashGlow.localPosition = Vector3.up * .05f;
            trashGlow.localRotation = Quaternion.Euler(90f, 0f, 0f);
            trashGlow.gameObject.SetActive(false);
            trashBasket.gameObject.SetActive(false);
        }

        // Tests: crumple the card in the hand (as a fist would).
        public void SimulateCrumple() { if (carriedCard != null) Crumple(carriedCard); }
    }
}
