using UnityEngine;

namespace Windups.Handoff
{
    // F26: the user's hands are the tape measure. Two hands only: pinch with both and pull apart;
    // letting one go keeps the length. (A one-hand long pinch and the flat-palms size gesture do not
    // measure: the hands do other things too, so only a deliberate two-hand pinch counts.)
    // Pure logic: fed both hands' pinch state, pinch points and index tips every frame.
    public sealed class HandTape
    {
        public enum Mode { Idle, TwoHands, Measured }
        public const float MinTwoHands = .25f;

        public Mode State { get; private set; }
        public Vector3 A { get; private set; }
        public Vector3 B { get; private set; }
        public float Length => Vector3.Distance(A, B);
        public bool JustMeasured { get; private set; }

        private float twoSince = -1f;

        public void Reset()
        {
            State = Mode.Idle;
            JustMeasured = false;
            twoSince = -1f;
        }

        public void SetMeasured(Vector3 a, Vector3 b)
        {
            A = a;
            B = b;
            State = Mode.Measured;
            JustMeasured = true;
        }

        // F39: the ends after snapping (to an axis, an edge, the desk top); the state stays as it is.
        public void Snap(Vector3 a, Vector3 b)
        {
            A = a;
            B = b;
        }

        public void Step(float now, bool pinch0, Vector3 point0, Vector3 tip0, bool pinch1, Vector3 point1, Vector3 tip1)
        {
            JustMeasured = false;
            if (pinch0 && pinch1)
            {
                if (State != Mode.TwoHands) { State = Mode.TwoHands; twoSince = now; }
                A = point0;
                B = point1;
            }
            else if (State == Mode.TwoHands)
            {
                // One hand let go: keep the length if both held it long enough.
                if (now - twoSince >= MinTwoHands) { State = Mode.Measured; JustMeasured = true; }
                else State = Mode.Idle;
            }
        }
    }
}
