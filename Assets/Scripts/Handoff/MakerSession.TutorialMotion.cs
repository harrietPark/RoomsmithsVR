using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    // F52: the lesson card has no speech panel of its own beside it (the user called it a strange "explanation"
    // panel); the teacher's own speech bubble carries the line (TutorialCaptionFrame).
    public sealed partial class MakerSession
    {
        private RawImage lessonNextImage;
        private Transform lessonTimer, lessonTimerFill;

        private void BuildLessonMotion()
        {
            lessonNextImage = Instantiate(card.image, card.image.transform.parent);
            lessonNextImage.name = "Next hand pose";
            lessonNextImage.raycastTarget = false;
            lessonNextImage.color = new Color(1f, 1f, 1f, 0f);
            if (CardName != "tape") return;
            // F77: a paper label (not a button), left of the Skip in the corner.
            lessonTimer = PaperTag("Example size and settle timer", .064f, .026f, Color.white, out var label, 10f, false);
            lessonTimer.SetParent(card.root, false);
            lessonTimer.localPosition = new Vector3(-.014f, .047f, -.008f);
            label.text = "130 mm";
            label.color = Blueprint;
            var track = ToyMeshes.RoundedSlab(.05f, .0018f, .0009f, .001f, new Color(.8f,.85f,.95f), Color.white, Color.white, "LessonTimerTrack");
            owned.Add(track);
            var t = Part("Timer track", track);
            t.SetParent(lessonTimer, false);
            t.localPosition = new Vector3(0f, -.009f, -.003f);
            var fill = ToyMeshes.RoundedSlab(.05f, .0018f, .0009f, .001f, Blueprint, Blueprint, Blueprint, "LessonTimerFill");
            owned.Add(fill);
            lessonTimerFill = Part("Timer fill", fill);
            lessonTimerFill.SetParent(lessonTimer, false);
            lessonTimerFill.localPosition = new Vector3(0f, -.009f, -.004f);
        }

        private static Rect LessonUV(int frame) => new Rect(frame % 2 * .5f + .002f, (frame < 2 ? .5f : 0f) + .002f, .496f, .496f);

        private void AnimateLessonMotion()
        {
            if (!CardShown) return;
            float elapsed = Time.time - cardSince;
            int[] frames = CardFrames;
            string name = CurrentLesson?.name ?? "";
            if (name == "palm up") frames = new[] { 0, 0 };
            else if (name == "tape pinch") frames = new[] { 0, 0 };
            else if (name == "tape pull") frames = new[] { 0, 1, 1 };
            else if (name == "tape keep") frames = new[] { 1, 2, 3, 3, 3 };
            float tick = elapsed / CardFrameSeconds;
            int i = (int)tick % frames.Length, frame = frames[i], next = frames[(i + 1) % frames.Length];
            float cross = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((tick - Mathf.Floor(tick) - .74f) / .26f));
            card.image.uvRect = LessonUV(frame);
            if (lessonNextImage != null)
            {
                lessonNextImage.uvRect = LessonUV(next);
                lessonNextImage.color = new Color(1f, 1f, 1f, cross);
            }
            string[] titles = CardName == "colour"
                ? new[] { "Left palm: palette", "Pinch the object", "Release to collect", "Colour on your palette" }
                : CardName == "texture" ? new[] { "Frame the surface", "Hold still", "Move hands away", "Texture collected" }
                // F84: the three sculpt cards (before -> after, the old outline dashed)
                : CardName == "sculpt_push" ? new[] { "Point at the clay", "Touch it", "Push in", "Dented" }
                : CardName == "sculpt_pull" ? new[] { "Pinch the clay", "Hold the pinch", "Pull it out", "Stretched" }
                : CardName == "sculpt_smooth" ? new[] { "Sponge finger (left)", "Touch the rough part", "Rub along", "Smooth" }
                : new[] { "Pinch both hands", "Pull apart", "Release one hand", "Keep still for 3 seconds" };
            card.label.text = titles[frame];
            if (lessonTimer != null)
            {
                lessonTimer.gameObject.SetActive(frame > 0);
                float progress = frame < 2 ? 0f : Mathf.Clamp01((elapsed % (frames.Length * CardFrameSeconds) - CardFrameSeconds) / 3f);
                lessonTimerFill.localScale = new Vector3(Mathf.Max(.001f, progress), 1f, 1f);
                lessonTimerFill.localPosition = new Vector3(-.025f * (1f - progress), -.009f, -.004f); // (local to the timer)
            }
        }
    }
}
