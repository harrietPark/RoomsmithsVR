using System.Collections;
using UnityEngine;

namespace Windups.Handoff
{
    // F82 (2026-11-14, the user after the Quest try: "the note jumping onto the wall is too sudden - someone should go and
    // stick it there, saying so"). When the note is confirmed, Fixer says it will put it up, the note flies into Fixer's
    // raised hands and rides above its head while Fixer takes a few steps toward the board (on its own surface, a free
    // spot, never more than NoteWalkMost), then Fixer hops and the note flies on to the board and sticks with its slap.
    // Fixer walks back to its seat. Taken by the person on the way, it is theirs (as before). TeammatePostsNote = false
    // keeps the note flying straight to the board.
    public sealed partial class MakerSession
    {
        public static bool TeammatePostsNote = true;
        public const float NoteWalkMost = .25f;
        public bool NotePosting { get; private set; }
        public int NotesPostedByTeammate { get; private set; }

        private IEnumerator PostByTeammate(Transform view)
        {
            const Crew poster = Crew.Fixer;
            if (!TeammatePostsNote || board == null || view == null || !cast.TryGetValue(poster, out var c) || c == null || c.movement == null || c.movement.IsHeld)
            {
                yield return FlyToCurrent(view);
                yield break;
            }
            NotePosting = true;
            var pet = c.movement;
            Vector3 home = pet.transform.position;
            Ack(poster, WallPlace == "wall" ? "I'll stick it up on the wall!" : "I'll put it up for you!");
            Body(poster)?.Play("HandRaise");
            // 1. Into Fixer's raised hands.
            Vector3 from = view.position, fromScale = view.localScale;
            Quaternion fromTurn = view.rotation;
            for (float t = 0f; t < 1f; t += Time.deltaTime / .45f)
            {
                if (view == null || view == carriedNote) { NotePosting = false; yield break; }
                Vector3 hands = OverHead(c);
                float e = Mathf.SmoothStep(0f, 1f, t);
                view.position = Vector3.Lerp(from, hands, e) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .06f;
                view.rotation = Quaternion.Slerp(fromTurn, SpeechBubbleDock.FacingAt(hands), e);
                view.localScale = Vector3.Lerp(fromScale, fromScale * .7f, e);
                yield return null;
            }
            ToySounds.Play(ToySounds.Cue.Paper, view.position, .45f, 1.3f);
            // 2. A few steps toward the board, the note held up.
            Vector3 toBoard = Vector3.ProjectOnPlane(board.position - pet.transform.position, Vector3.up);
            bool walked = false;
            if (toBoard.magnitude > .15f)
            {
                Vector3 want = pet.transform.position + toBoard.normalized * Mathf.Min(NoteWalkMost, toBoard.magnitude - .1f);
                if (FreeStand(poster, want, .12f, out var spot, null, false))
                {
                    pet.UnlockMeetingSeat();
                    walked = pet.GoTo(spot, 1.6f);
                }
            }
            for (float until = Time.time + (walked ? 3f : .3f); Time.time < until; )
            {
                if (view == null || view == carriedNote) { NotePosting = false; if (walked) WalkHome(poster, home); yield break; }
                view.position = Vector3.Lerp(view.position, OverHead(c), 1f - Mathf.Exp(-Time.deltaTime * 14f));
                view.rotation = SpeechBubbleDock.FacingAt(view.position);
                if (walked && pet.State != RoomCapsule.CapsulePetController.MotionState.Walking && Time.time > until - 2.6f) break;
                yield return null;
            }
            // 3. A hop and up it goes.
            if (view == null || view == carriedNote) { NotePosting = false; if (walked) WalkHome(poster, home); yield break; }
            StartCoroutine(Bounce(c, 1, .03f));
            yield return new WaitForSeconds(.12f);
            ToySounds.Play(ToySounds.Cue.Whoosh, view.position, .4f, 1.4f);
            NotesPostedByTeammate++;
            WriteInputLog("note posted by " + MakerRoles.Name(poster) + (walked ? " (walked toward the board)" : ""), true);
            yield return FlyToCurrent(view);
            Body(poster)?.PlaySoon("React_Nod", .1f);
            NotePosting = false;
            yield return new WaitForSeconds(.6f);
            if (walked) WalkHome(poster, home);
        }

        // Just over a teammate's head, a little toward the person (held up in both mittens).
        private Vector3 OverHead(WindupCharacter c)
        {
            var body = WindupBody.Of(c);
            float top = body != null ? body.HeadTop.y : c.transform.position.y + .156f;
            Vector3 toPerson = Head != null ? Vector3.ProjectOnPlane(Head.position - c.transform.position, Vector3.up) : Vector3.back;
            toPerson = toPerson.sqrMagnitude > 1e-4f ? toPerson.normalized : Vector3.back;
            return new Vector3(c.transform.position.x, top + .07f, c.transform.position.z) + toPerson * .02f;
        }

        private void WalkHome(Crew crew, Vector3 home)
        {
            if (!cast.TryGetValue(crew, out var c) || c == null || c.movement == null || c.movement.IsHeld) return;
            c.movement.GoTo(ClearSeat(crew, home), 2f);
            StartCoroutine(SeatWhenBack(c.movement));
        }
    }
}
