using UnityEngine;

namespace Windups.Handoff
{
    // F68 (Quest 2026-11-14: "every button should work from a distance and show its indicator, the hand ray and the
    // circle"). The hand's ray (FarAim) knew the intro arrows, the crew's tools, the notebook, the Done pill and the
    // pictures; the reply pills, Find ideas and the settings box were pressed from afar but showed no ring, and the
    // stickers, the v2 review pictures and the sketchbook page's check and eraser had neither. They are all Tap
    // targets now: RemoteGrab draws the ray and the ring on them, and a pinch presses them from afar.
    public sealed partial class MakerSession
    {
        public const float PageButtonRay = .016f;

        // A button of the session under the hand's ray (and how far): the first such, as OnPinch would press it.
        private bool FarButtonOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            var q = Question;
            if (q != null && q.root != null && !q.answered && ReplyOnRay(ray) >= 0 &&
                new Plane(q.root.forward, q.root.position).Raycast(ray, out float rd)) { distance = rd; return true; }
            if (IdeasOnRay(ray, out distance)) return true;
            if (SettingsOnRay(ray, out distance)) return true;
            var sticker = StickerOnRay(ray);
            if (sticker != null && sticker.root != null) { distance = Vector3.Distance(ray.origin, sticker.root.position); return true; }
            if (ReviewPictureOnRay(ray, out distance) != null) return true;
            if (PageButtonOnRay(ray, out distance, out _)) return true;
            if (WallButtonOnRay(ray, out distance)) return true; // F72: Rewrite on the note, the x on a picture or card
            if (CardSkipOnRay(ray, out distance)) return true;   // F77: the lesson card's Skip
            if (RePickOnRay(ray, out distance)) return true;     // F81: Change sketch on the chosen picture
            if (PlatePagerOnRay(ray, out distance) != 0) return true; // F94: < > on the stand
            distance = 0f;
            return false;
        }

        private bool IdeasOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            if (!IdeasButtonShown || ideasPressing) return false;
            if (!new Plane(ideasButton.forward, ideasPill.position).Raycast(ray, out float d) || d <= 0f || d > 4f) return false;
            if (!OnIdeasPill(ray.GetPoint(d), .004f + d * .012f, -.01f, .01f)) return false; // F82: same slack as its pinch
            distance = d;
            return true;
        }

        private bool SettingsOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            if (!SettingsOpen || !new Plane(settingsBox.forward, settingsBox.position).Raycast(ray, out float d) || d <= 0f || d > 4f) return false;
            var p = settingsBox.InverseTransformPoint(ray.GetPoint(d));
            if (Mathf.Abs(p.x) > BoxW * .5f || Mathf.Abs(p.y) > BoxH * .5f) return false;
            distance = d;
            return true;
        }

        // The open page's check (or its undo, F71) under the ray from afar (a hand right at the page draws or touches it).
        private bool PageButtonOnRay(Ray ray, out float distance, out bool check)
        {
            distance = 0f;
            check = false;
            if (!Drawing || paper == null || paperDone == null || !paper.gameObject.activeInHierarchy) return false;
            if (!new Plane(paper.forward, paper.position).Raycast(ray, out float d) || d <= .15f || d > 4f) return false;
            Vector3 at = ray.GetPoint(d);
            float slack = .004f + d * .01f;
            if (Vector3.Distance(at, paperDone.position) < PageButtonRay + slack) { distance = d; check = true; return true; }
            if (paperUndo != null && Vector3.Distance(at, paperUndo.position) < PageButtonRay + slack) { distance = d; return true; }
            return false;
        }

        // OnPinch: the page's check or undo pressed from afar (F71: it sinks, lights and ripples as under a fingertip).
        private bool PageButtonPinch(Ray? ray)
        {
            if (!ray.HasValue || !PageButtonOnRay(ray.Value, out _, out bool check)) return false;
            if (check) PressPaperDone();
            else PressPaperUndo();
            return true;
        }

        // Checks: what the ray of this side's hand is on ("Tap", "Movable", ...), as RemoteGrab's ring shows it.
        public bool FarButtonUnder(Ray ray) => FarButtonOnRay(ray, out _);
    }
}
