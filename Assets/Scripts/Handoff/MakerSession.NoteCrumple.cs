using UnityEngine;

namespace Windups.Handoff
{
    // F98 (Quest 2026-11-15, the user: "let me crumple the post-its too"): a post-it carried off the idea wall is crumpled
    // the way a picture is (F72): the holding hand closes into a fist for a moment, or the other hand pinches it too. It
    // becomes a paper ball in the hand; let go by Fixer's basket (or thrown at it) it goes in, a miss lies where it fell.
    // The idea is gone from the wall (maker_wall.json). The note of the work in progress is not crumpled (its make
    // needs it); once that work is finished (or put aside) it can be.
    public sealed partial class MakerSession
    {
        public int NotesCrumpled { get; private set; }
        private float noteFistSince = -1f;

        // The carried note may be thrown away now.
        private bool NoteCrumplable(Transform view) => view != null && (view != ringNote || WorkArchived || !Begun);

        // Called from NoteCarryFrame while a note is in the fingers: true when it was crumpled.
        private bool NoteCrumpleGesture()
        {
            var view = carriedNote;
            if (view == null || noteHand == null || noteTarget.HasValue || !NoteCrumplable(view)) { noteFistSince = -1f; return false; }
            if (HandFist(noteHand))
            {
                if (noteFistSince < 0f) noteFistSince = Time.time;
                if (Time.time - noteFistSince > .3f) { CrumpleCarriedNote(); return true; }
            }
            else noteFistSince = -1f;
            foreach (var other in HandSet.Both())
            {
                if (other == null || other == noteHand || !other.isActiveAndEnabled || !HandIO.Connected(other) || !HandIO.Pinching(other)) continue;
                if (Vector3.Distance(PinchPoint(other), view.position) < WallSheet * view.lossyScale.x * .6f + .02f) { CrumpleCarriedNote(); return true; }
            }
            return false;
        }

        public void CrumpleCarriedNote()
        {
            var view = carriedNote;
            if (view == null || !NoteCrumplable(view)) return;
            var hand = noteHand;
            var data = carriedData;
            Vector3 at = view.position;
            carriedNote = null;
            carriedData = null;
            noteHand = null;
            noteTarget = null;
            noteOverRing = false;
            noteFistSince = -1f;
            wallViews.Remove(view);
            looseNotes.Remove(view);
            noteOf.Remove(view);
            if (view == ringNote) { StripCurrent(view); ringNote = null; ringData = null; }
            if (data != null && Wall.notes.Remove(data)) SaveWall();
            Destroy(view.gameObject);
            LayOutWall(false);
            // The ball stays in the hand (the picture's ball path: basket, throw, miss).
            if (pinCarry == null) pinCarry = new PinCarry { ball = MakePinBall(at), hand = hand };
            else MakePinBall(at).Throw(Vector3.zero, Random.insideUnitSphere * 4f);
            NotesCrumpled++;
            Talk.Add("The person crumpled a post-it from the wall" + (data != null ? ": " + data.text : "."));
            WriteInputLog("wall note crumpled" + (data != null ? ": " + data.text : ""), true);
        }
    }
}
