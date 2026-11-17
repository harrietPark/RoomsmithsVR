using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    // F81 (2026-11-14, the user after the Quest try: "the buttons are sometimes hidden behind speech bubbles - a button
    // must never be covered by a bubble, it must be on top"). A group of buttons (the reply pills, the shape step's Done,
    // Find ideas) that a speech card overlaps as the person sees it is brought ButtonAhead nearer to the eyes than that card,
    // along the line of sight (so it stays where it was for the person, only in front). Checked against every teammate's
    // speech card and the introduction's profile cards; the result is cached for ButtonsFrontEvery.
    public sealed partial class MakerSession
    {
        public const float ButtonAhead = .03f, ButtonsAheadMost = .35f;
        // 2026-11-15 (second Quest try: "sometimes the buttons cover the speech bubble - they must always be above it"):
        // a group that overlaps a speech card (or a revealed intro card) as the person sees it now steps UP until its
        // bottom is ButtonClearance above that card's top (again for a card it then meets), instead of coming in front
        // of it and covering its words. At most ButtonsLiftMost; ButtonsInFront = true brings back the F81 rule.
        public static bool ButtonsInFront;
        public const float ButtonClearance = .012f, ButtonsLiftMost = .3f;
        public int ButtonsBroughtForward { get; private set; }
        public int ButtonsLifted { get; private set; }
        private static readonly Vector3[] buttonCorners = new Vector3[4];
        private readonly List<Rect> bubbleRects = new List<Rect>(8);
        private readonly List<float> bubbleDistances = new List<float>(8);

        // How far the group whose middle is `centre` (half size in its own plane) must move: up above the speech cards
        // it overlaps (F81: toward the eyes, in front of them).
        private Vector3 AheadOfBubbles(Vector3 centre, float halfW, float halfH)
        {
            var view = Camera.main;
            if (view == null || Head == null) return Vector3.zero;
            Quaternion turn = SpeechBubbleDock.FacingAt(centre);
            Vector3 right = turn * Vector3.right, up = turn * Vector3.up;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            buttonCorners[0] = centre - right * halfW - up * halfH;
            buttonCorners[1] = centre + right * halfW - up * halfH;
            buttonCorners[2] = centre - right * halfW + up * halfH;
            buttonCorners[3] = centre + right * halfW + up * halfH;
            for (int i = 0; i < 4; i++)
            {
                Vector3 v = view.WorldToViewportPoint(buttonCorners[i]);
                if (v.z <= 0f) return Vector3.zero;
                x0 = Mathf.Min(x0, v.x); y0 = Mathf.Min(y0, v.y); x1 = Mathf.Max(x1, v.x); y1 = Mathf.Max(y1, v.y);
            }
            var rect = Rect.MinMaxRect(x0, y0, x1, y1);
            float distance = Vector3.Distance(Head.position, centre);
            // the cards that can be in the way, as the person sees them
            bubbleRects.Clear();
            bubbleDistances.Clear();
            foreach (var pair in cast)
            {
                var bubble = pair.Value != null ? pair.Value.authoredBubble : null;
                if (bubble == null || !bubble.gameObject.activeInHierarchy || !ViewRect(view, bubble.transform, true, out var r)) continue;
                bubbleRects.Add(r);
                bubbleDistances.Add(ButtonsInFront ? NearestDistance(bubble.transform) : 0f);
            }
            if (arrival != null)
                foreach (var card in arrival.IntroCards)
                    if (card != null && card.Current == CharacterIntroCard.State.Revealed)
                    {
                        Vector3 v = view.WorldToViewportPoint(card.RevealedAt);
                        var half = card.CardSize * .5f;
                        float d = Vector3.Distance(Head.position, card.RevealedAt);
                        float sx = half.x / Mathf.Max(.05f, d) * .9f, sy = half.y / Mathf.Max(.05f, d) * .9f; // (about the card's size in view)
                        if (v.z > 0f) { bubbleRects.Add(Rect.MinMaxRect(v.x - sx, v.y - sy, v.x + sx, v.y + sy)); bubbleDistances.Add(d); }
                    }
            if (ButtonsInFront)
            {
                float need = 0f;
                for (int i = 0; i < bubbleRects.Count; i++) if (bubbleRects[i].Overlaps(rect)) need = Mathf.Max(need, distance - (bubbleDistances[i] - ButtonAhead));
                if (need <= .002f) return Vector3.zero;
                ButtonsBroughtForward++;
                Vector3 toEyes = Head.position - centre;
                return toEyes.normalized * Mathf.Min(need, Mathf.Min(ButtonsAheadMost, toEyes.magnitude - .2f));
            }
            // Up: viewport height is 2 d tan(fov / 2) metres at the group's distance.
            float metresPerView = 2f * distance * Mathf.Tan(view.fieldOfView * .5f * Mathf.Deg2Rad);
            float clearance = ButtonClearance / Mathf.Max(.01f, metresPerView), lift = 0f, most = ButtonsLiftMost / Mathf.Max(.01f, metresPerView);
            for (int pass = 0; pass < 4; pass++)
            {
                var moved = new Rect(rect.x, rect.y + lift, rect.width, rect.height);
                float need = 0f;
                foreach (var r in bubbleRects) if (r.Overlaps(moved)) need = Mathf.Max(need, r.yMax + clearance - moved.yMin);
                if (need <= 1e-4f) break;
                lift += need;
                if (lift >= most) { lift = most; break; }
            }
            if (lift <= 1e-4f) return Vector3.zero;
            ButtonsLifted++;
            return Vector3.up * lift * metresPerView;
        }

        // The distance from the eyes to the nearest part of a speech card (its renderers' bounds).
        private float NearestDistance(Transform card)
        {
            float best = Vector3.Distance(Head.position, card.position);
            tagRenderers.Clear();
            card.GetComponentsInChildren(false, tagRenderers);
            foreach (var r in tagRenderers)
                if (r != null && r.enabled) best = Mathf.Min(best, Mathf.Sqrt(r.bounds.SqrDistance(Head.position)));
            return best;
        }
    }
}
