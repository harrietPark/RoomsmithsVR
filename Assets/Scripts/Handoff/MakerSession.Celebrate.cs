using UnityEngine;

namespace Windups.Handoff
{
    // F82 (2026-11-14, the user: "when we celebrate, I'd like particles - confetti like this picture, but in our app's
    // style"). A shower of paper confetti in the crew's colours and the toy pastels (ToyFx.ConfettiShower) bursts up
    // between the person and the work and flutters down: the finished work (ending) gets the full shower, the first
    // make revealed a smaller one. Pooled pieces only (no particle system, nothing allocated per frame).
    public sealed partial class MakerSession
    {
        public int Celebrations { get; private set; }

        private void Celebrate(Vector3 over, float width = .3f, int count = 60) // F82: narrow (the user: "not so wide")
        {
            Celebrations++;
            Vector3 toViewer = Head != null ? Head.position - over : Vector3.back;
            // Halfway between the work and the eyes, a little above the work, so the pieces fall across the view.
            Vector3 at = Head != null ? Vector3.Lerp(over, Head.position, .45f) : over;
            if (Head != null) at.y = Mathf.Max(over.y + .05f, Head.position.y + .1f); // starting a little above the eyes, it falls across the view
            ToyFx.ConfettiShower(at, toViewer, width, count);
            ToySounds.Play(ToySounds.Cue.Pop, at, .6f, 1.25f);
        }

        public void SimulateCelebrate(Vector3 over) => Celebrate(over);
    }
}
