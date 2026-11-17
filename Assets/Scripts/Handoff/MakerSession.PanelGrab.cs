using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // 2026-11-13 (the user after a Quest try):
    //  - "when Maker gives the 3 reference pictures they keep following Maker - make them stay in one place": the look
    //    row is placed once, over Maker's speech card as before, and then stays there (KeepRowOverBubble);
    //  - "the UI covers the speech bubbles; those should be grabbable and movable too" (they chose: the covering UI,
    //    not the bubbles): the look row and the two review pictures (previous / next shape) are pinched (near, or
    //    aimed with the hand's ray, or the Editor mouse) and carried; let go they stay. A pinch that does not move
    //    PanelDragStart is still a tap (it picks the look, as before). And until the person has moved them, a group
    //    standing in front of a speech card as the person sees it steps back along the line of sight until the card is
    //    in front (PanelGuardFrame), and comes forward again a while after the card has gone.
    public sealed partial class MakerSession
    {
        public const float PanelDragStart = .02f, PanelBehind = .04f, PanelPushMost = .3f, PanelPushHold = 2f;
        // F80 (Quest 2026-11-14: "Maker said pick one; when I picked, all 3 pictures suddenly moved and I couldn't select
        // one"): a pinch on a picture that drifted 2 cm (the hand landing, or the ray swinging at a distance) started
        // carrying the whole row, and since F67 a short carry stayed stuck to the open hand. Now the look row is carried
        // only after the pinch has been held LookRowDragAfter and moved LookRowDragStart (plus LookRowFarSlack per metre
        // of ray), it is never grab & release (HeldBy "look row"), and while a hand is near the row or aims at it the row
        // stays put (no rising over pills, no stepping back: RowReached).
        public const float LookRowDragStart = .06f, LookRowFarSlack = .06f, LookRowDragAfter = .3f, RowFreezeReach = .2f;
        // F67 (the user: "I'd like to grab and move the final picture with my hand too"): the chosen picture (beside
        // Maker after a pick) is carried the same way; it stays where it is let go.
        private enum PanelGroup { None, LookRow, Review, Chosen, Journey }

        // The picked picture, kept beside Maker once the step moved on.
        private Picture ChosenPicture => Current != Step.Looks && Look != null && Looks.Count == 1 && Looks[0].root != null && Looks[0].root.gameObject.activeInHierarchy ? Looks[0] : null;

        private Picture ChosenAt(Vector3 point)
        {
            var p = ChosenPicture;
            if (p == null || float.IsInfinity(point.x)) return null;
            // (local units: the picture's own size; the root is scaled down beside Maker)
            Vector3 local = p.root.InverseTransformPoint(point);
            float depth = .04f / Mathf.Max(.1f, p.root.lossyScale.x);
            return Mathf.Abs(local.x) < p.size * .6f && Mathf.Abs(local.y) < p.size * .65f && Mathf.Abs(local.z) < depth ? p : null;
        }

        private Picture ChosenOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            var p = ChosenPicture;
            if (p == null || !new Plane(p.root.forward, p.root.position).Raycast(ray, out float d) || d <= 0f || d > 4f) return null;
            Vector3 local = p.root.InverseTransformPoint(ray.GetPoint(d));
            if (Mathf.Abs(local.x) > p.size * .6f || Mathf.Abs(local.y) > p.size * .65f) return null;
            distance = d;
            return p;
        }

        public int PanelDrags { get; private set; }
        public int PanelTaps { get; private set; }
        public int PanelsSteppedBack { get; private set; }
        public bool PanelHeld => panelGroup != PanelGroup.None && panelDragging;
        public Vector3 RowAnchor => rowAnchor;
        public float RowPush => rowPush;
        public float ReviewPush => reviewPush;
        public Vector3? ReviewCentre => reviewPanelCenter;

        // ------------------------------------------------------------ the look row's place
        private Transform rowFor;            // the first picture of the row that was placed (a new row is placed anew)
        private bool rowMovedByPerson, reviewMovedByPerson;
        private Vector3 rowAnchor;
        private float rowPush, rowPushWant, rowPushHeld, reviewPush, reviewPushWant, reviewPushHeld;
        private readonly Dictionary<Picture, float> reviewSide = new Dictionary<Picture, float>();

        // The row was placed for these pictures (else it is placed now).
        private bool RowPlacedFor(Transform first) => rowFor != null && rowFor == first;

        private Vector3 PushDir(Vector3 at)
        {
            Vector3 d = Head != null ? at - Head.position : Vector3.forward;
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
        }

        // ------------------------------------------------------------ the review pictures

        private void ReviewPanelsFrame()
        {
            if (!reviewPanelCenter.HasValue) return;
            Vector3 centre = reviewPanelCenter.Value + PushDir(reviewPanelCenter.Value) * reviewPush;
            Quaternion rot = SpeechBubbleDock.FacingAt(centre);
            PlaceReviewPicture(shapeReference, centre, rot);
            PlaceReviewPicture(revisionPicture, centre, rot);
        }

        private void PlaceReviewPicture(Picture p, Vector3 centre, Quaternion rot)
        {
            if (p == null || p.root == null || !reviewSide.TryGetValue(p, out float side)) return;
            Vector3 at = centre + rot * Vector3.right * side;
            p.root.SetPositionAndRotation(Vector3.Lerp(p.root.position, at, 1f - Mathf.Exp(-Time.deltaTime * 12f)), rot);
        }

        private Picture ReviewPictureAt(Vector3 point)
        {
            if (float.IsInfinity(point.x)) return null;
            foreach (var p in new[] { revisionPicture, shapeReference })
            {
                if (p == null || p.root == null || !p.root.gameObject.activeInHierarchy) continue;
                Vector3 local = p.root.InverseTransformPoint(point);
                if (Mathf.Abs(local.x) < p.size * .6f && Mathf.Abs(local.y) < p.size * .65f && Mathf.Abs(local.z) < .04f) return p;
            }
            return null;
        }

        private Picture ReviewPictureOnRay(Ray ray, out float distance)
        {
            distance = float.PositiveInfinity;
            Picture best = null;
            foreach (var p in new[] { revisionPicture, shapeReference })
            {
                if (p == null || p.root == null || !p.root.gameObject.activeInHierarchy) continue;
                if (!new Plane(p.root.forward, p.root.position).Raycast(ray, out float d) || d <= 0f || d >= distance) continue;
                Vector3 local = p.root.InverseTransformPoint(ray.GetPoint(d));
                if (Mathf.Abs(local.x) > p.size * .6f || Mathf.Abs(local.y) > p.size * .65f) continue;
                distance = d;
                best = p;
            }
            return best;
        }

        // ------------------------------------------------------------ pinch, carry, let go

        private PanelGroup panelGroup;
        private Hand panelHand;
        private bool panelDragging, panelFar;
        private Picture panelPicture;
        private Vector3 panelStart, panelStartAnchor;
        private float panelStartDistance, panelStartDepth, panelStartTime;

        // A pinch (or click) on the look row or a review picture: carried if it moves, a tap if it does not.
        private bool PanelPinch(Hand hand, Vector3 point, Ray? ray)
        {
            bool row = Current == Step.Looks && Look == null && Looks.Count > 0 && rowFor != null;
            Picture picture = null;
            var group = PanelGroup.None;
            float distance = 0f;
            bool far = false;
            var near = row ? PictureAt(point) : null;
            var journey = JourneyAt(point, ray, out float journeyDistance);
            if (journey != null) { picture = journey; group = PanelGroup.Journey; distance = journeyDistance; far = journeyDistance > 0; }
            else if (near != null && Looks.Contains(near)) { picture = near; group = PanelGroup.LookRow; }
            else if ((near = ReviewPictureAt(point)) != null) { picture = near; group = PanelGroup.Review; }
            else if ((near = ChosenAt(point)) != null) { picture = near; group = PanelGroup.Chosen; } // F67
            else if (ray.HasValue)
            {
                float d1 = float.PositiveInfinity;
                var aimed = row ? PictureOnRay(ray.Value, out d1) : null;
                var review = ReviewPictureOnRay(ray.Value, out float d2);
                var chosen = ChosenOnRay(ray.Value, out float d3);
                if (aimed != null && Looks.Contains(aimed) && (review == null || d1 <= d2)) { picture = aimed; group = PanelGroup.LookRow; distance = d1; far = true; }
                else if (review != null) { picture = review; group = PanelGroup.Review; distance = d2; far = true; }
                else if (chosen != null) { picture = chosen; group = PanelGroup.Chosen; distance = d3; far = true; }
            }
            if (group == PanelGroup.None) return false;
            panelGroup = group;
            panelPicture = picture;
            panelHand = hand;
            panelDragging = false;
            panelFar = far;
            panelStartAnchor = group == PanelGroup.LookRow ? rowAnchor : group == PanelGroup.Chosen || group == PanelGroup.Journey ? picture.home : reviewPanelCenter ?? picture.root.position;
            panelStart = far ? ray.Value.GetPoint(distance) : point;
            panelStartDistance = distance;
            panelStartTime = Time.time;
            // F67: the picked picture has no tap meaning: a pinch takes hold of it at once (so grab & release applies).
            if (group == PanelGroup.Chosen || group == PanelGroup.Journey) { panelDragging = true; PanelDrags++; WriteInputLog("carry " + PanelName(group), true); }
            panelStartDepth = hand != null && Head != null && !float.IsInfinity(point.x) ? Vector3.Dot(point - Head.position, Head.forward) : 0f;
            return true;
        }

        // Each frame while the pinch (or mouse button) is held.
        private void PanelFrame(bool pinching, Vector3 point, Ray? ray)
        {
            if (panelGroup == PanelGroup.None) return;
            if (panelGroup == PanelGroup.LookRow && (Current != Step.Looks || Look != null || Looks.Count == 0)) { EndPanel(false); return; }
            if (panelGroup == PanelGroup.Review && !reviewPanelCenter.HasValue) { EndPanel(false); return; }
            if (panelGroup == PanelGroup.Chosen && ChosenPicture != panelPicture) { EndPanel(false); return; }
            Vector3 now = panelStart;
            if (!panelFar) { if (!float.IsInfinity(point.x)) now = point; }
            else if (ray.HasValue)
            {
                // Like the far drag of a card (RemoteGrab): along the ray, pushed in or out as the hand moves.
                float depth = panelHand != null && Head != null && !float.IsInfinity(point.x) ? Vector3.Dot(point - Head.position, Head.forward) : panelStartDepth;
                now = ray.Value.GetPoint(Mathf.Clamp(panelStartDistance + (depth - panelStartDepth) * 2.5f, .25f, 3f));
            }
            Vector3 delta = now - panelStart;
            bool row = panelGroup == PanelGroup.LookRow;
            float start = row ? LookRowDragStart + (panelFar ? panelStartDistance * LookRowFarSlack : 0f) : PanelDragStart;
            if (!panelDragging && delta.magnitude > start && (!row || Time.time - panelStartTime >= LookRowDragAfter))
            {
                panelDragging = true;
                PanelDrags++;
                ToySounds.Play(ToySounds.Cue.Paper, panelPicture.root.position, .35f, 1.3f);
                WriteInputLog("carry " + PanelName(panelGroup), true);
            }
            if (panelDragging)
            {
                Vector3 to = panelStartAnchor + delta;
                if (panelGroup == PanelGroup.LookRow) { rowAnchor = to; rowMovedByPerson = true; rowPush = rowPushWant = 0f; }
                else if (panelGroup == PanelGroup.Chosen) { panelPicture.home = to; ChosenMovedByPerson = true; }
                else if (panelGroup == PanelGroup.Journey) { panelPicture.home = to; panelPicture.root.position = to; }
                else { reviewPanelCenter = to; reviewMovedByPerson = true; reviewPush = reviewPushWant = 0f; }
            }
            if (!pinching) EndPanel(!panelDragging);
        }

        // F80: a hand within RowFreezeReach of a look picture, or aiming its ray at one: the row does not move under it.
        public bool RowReachedNow { get; private set; }
        private float rowReachedUntil;
        private bool RowReached()
        {
            bool now = panelGroup == PanelGroup.LookRow;
            if (!now)
                foreach (var hand in HandSet.Both())
                {
                    if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) continue;
                    if (HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip))
                        foreach (var look in Looks)
                            if (look != null && look.root != null && (look.root.position - tip.position).sqrMagnitude < RowFreezeReach * RowFreezeReach) { now = true; break; }
                    if (!now && HandIO.Pointer(hand, out Pose aim) && PictureOnRay(new Ray(aim.position, aim.forward), out _) != null) now = true;
                    if (now) break;
                }
            if (now) rowReachedUntil = Time.time + .6f; // (and a moment after, while the hand settles)
            RowReachedNow = Time.time < rowReachedUntil;
            return RowReachedNow;
        }

        private static string PanelName(PanelGroup g) => g == PanelGroup.LookRow ? "look row" : g == PanelGroup.Chosen ? "chosen picture" : "review pictures";
        public bool ChosenMovedByPerson { get; private set; }
        public Transform ChosenView => ChosenPicture?.root; // (checks)
        public bool PanelCarried => panelGroup != PanelGroup.None && panelDragging;
        public bool Flying => heldFlyAt >= 0f;

        private void EndPanel(bool tap)
        {
            var group = panelGroup;
            var picture = panelPicture;
            bool dragged = panelDragging;
            panelGroup = PanelGroup.None;
            panelPicture = null;
            panelHand = null;
            panelDragging = false;
            if (dragged && picture != null && picture.root != null)
            {
                if (group == PanelGroup.Journey) SaveJourneyCard(picture);
                ToySounds.Play(ToySounds.Cue.Press, picture.root.position, .3f, 1.2f);
                WriteInputLog("let go " + PanelName(group), true);
            }
            if (!tap || picture == null) return;
            PanelTaps++;
            if (group == PanelGroup.LookRow && Current == Step.Looks && Looks.Contains(picture)) PickLook(picture);
        }

        // ------------------------------------------------------------ out of the speech cards' way

        private float panelGuardAt;
        private readonly List<(Rect rect, float distance)> cardRects = new List<(Rect, float)>(4);

        private void PanelGuardFrame()
        {
            float blend = 1f - Mathf.Exp(-Time.deltaTime * 5f);
            rowPush = Mathf.Lerp(rowPush, rowPushWant, blend);
            reviewPush = Mathf.Lerp(reviewPush, reviewPushWant, blend);
            chosenPush = Mathf.Lerp(chosenPush, chosenPushWant, blend); // F102
            if (Time.time < panelGuardAt) return;
            panelGuardAt = Time.time + .25f;
            var view = Camera.main;
            if (view == null || Head == null) return;
            cardRects.Clear();
            foreach (var pair in cast)
            {
                var card = pair.Value != null ? pair.Value.authoredBubble : null;
                if (card != null && card.gameObject.activeInHierarchy && !card.IsHeld && ViewRect(view, card.transform, true, out var r))
                    cardRects.Add((r, Vector3.Distance(Head.position, card.transform.position)));
            }
            // An open question's reply pills are part of its speech card.
            if (Question != null && !Question.answered && Question.root != null && Question.root.gameObject.activeInHierarchy && ViewRect(view, Question.root, true, out var pills))
                cardRects.Add((pills, Vector3.Distance(Head.position, Question.root.position)));
            // The look row (while it waits for a pick and the person has not moved it).
            bool rowUp = rowFor != null && !rowMovedByPerson && Current == Step.Looks && Look == null && Looks.Count > 0;
            if (!rowUp || !RowReachedNow) // F80: not while a hand reaches for a picture
            {
                float rowNeed = rowUp ? PushNeeded(view, rowPush, true) : 0f;
                rowPushWant = Settle(rowNeed, rowPushWant, ref rowPushHeld);
            }
            bool reviewUp = reviewPanelCenter.HasValue && !reviewMovedByPerson;
            float reviewNeed = reviewUp ? PushNeeded(view, reviewPush, false) : 0f;
            reviewPushWant = Settle(reviewNeed, reviewPushWant, ref reviewPushHeld);
            // F102: the chosen picture (at eye level now) steps back from a speech card it covers too.
            var chosen = MakerSession.ChosenAtEyes && !ChosenMovedByPerson && panelGroup != PanelGroup.Chosen ? ChosenPicture : null;
            float chosenNeed = chosen != null && cardRects.Count > 0 ? Mathf.Clamp(NeedFor(view, chosen, chosenPush), 0f, PanelPushMost) : 0f;
            chosenPushWant = Settle(chosenNeed, chosenPushWant, ref chosenPushHeld);
        }

        private float chosenPush, chosenPushWant, chosenPushHeld;
        public float ChosenPush => chosenPush;

        // Keeps a step back for PanelPushHold after the last time it was needed (no breathing back and forth).
        private float Settle(float need, float want, ref float heldUntil)
        {
            if (need > want + .005f) { if (want < .005f) PanelsSteppedBack++; heldUntil = Time.time + PanelPushHold; return need; }
            if (need > .005f) { heldUntil = Time.time + PanelPushHold; return Mathf.Max(need, want); }
            return Time.time < heldUntil ? want : 0f;
        }

        // How far back (along the line of sight) a group must stand so that every speech card it overlaps, as the person
        // sees it, is in front of it; 0 when it covers none.
        private float PushNeeded(Camera view, float pushNow, bool row)
        {
            if (cardRects.Count == 0) return 0f;
            float need = 0f;
            if (row) foreach (var p in Looks) need = Mathf.Max(need, NeedFor(view, p, pushNow));
            else need = Mathf.Max(NeedFor(view, shapeReference, pushNow), NeedFor(view, revisionPicture, pushNow));
            return Mathf.Clamp(need, 0f, PanelPushMost);
        }

        private float NeedFor(Camera view, Picture p, float pushNow)
        {
            if (p == null || p.root == null || !p.root.gameObject.activeInHierarchy || !ViewRect(view, p.root, true, out var rect)) return 0f;
            float baseDistance = Vector3.Distance(Head.position, p.root.position) - pushNow, need = 0f;
            foreach (var card in cardRects)
                if (rect.Overlaps(card.rect)) need = Mathf.Max(need, card.distance + PanelBehind - baseDistance);
            return need;
        }
    }
}
