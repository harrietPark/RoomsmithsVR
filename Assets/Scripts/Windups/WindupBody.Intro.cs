using UnityEngine;

namespace Windups
{
    public sealed partial class WindupBody
    {
        // Temporary choreography only; never changes the model, controller or shared clips.
        public bool IntroActive { get; private set; }
        public float IntroLean { get; private set; }
        public float IntroNod { get; private set; }
        public float IntroWave { get; private set; }
        public float IntroWelcome { get; private set; }

        // F52: the intro shows each teammate's own tool (Spark's magnifier, Maker's brush, Fixer's tape): the
        // title sets the mitten poses directly, in this body's space (they win over wave/welcome).
        private Pose? introRight, introLeft;
        public bool IntroHandsSet => introRight.HasValue || introLeft.HasValue;
        public void IntroHands(Pose? right, Pose? left)
        {
            if (!IntroActive) return;
            introRight = right;
            introLeft = left;
        }

        public void BeginIntro()
        {
            IntroActive = true;
            IntroLean = IntroNod = IntroWave = IntroWelcome = 0;
            introRight = introLeft = null;
            OneShot = null; afterOneShot = null;
            CrossFade("Idle_Loop", false);
        }
        public void IntroPose(float lean = 0, float nod = 0, float wave = 0, float welcome = 0)
        {
            if (!IntroActive) return;
            IntroLean = Mathf.Clamp(lean, -6f, 6f);
            IntroNod = Mathf.Clamp(nod, 0, 7f);
            IntroWave = wave; IntroWelcome = Mathf.Clamp01(welcome);
        }
        public void EndIntro()
        {
            if (!IntroActive) return;
            IntroActive = false;
            IntroLean = IntroNod = IntroWave = IntroWelcome = 0;
            introRight = introLeft = null;
            Hold(true, null); Hold(false, null);
        }
        private void ApplyIntroPose()
        {
            if (!IntroActive) return;
            if (character.movement.IsHeld || character.movement.State == RoomCapsule.CapsulePetController.MotionState.Falling)
            { EndIntro(); return; }
            // Rotation is reset by the regular body facing pass each frame. Feet stay at the pivot.
            transform.rotation *= Quaternion.Euler(IntroLean, 0, 0);
            if (headBone != null && IntroNod > 0)
                headBone.rotation = Quaternion.AngleAxis(IntroNod, transform.right) * headBone.rotation;
            if (IntroHandsSet)
            {
                Hold(true, introRight, introRight.HasValue ? transform : null);
                Hold(false, introLeft, introLeft.HasValue ? transform : null);
            }
            else if (Mathf.Abs(IntroWave) > .0001f)
            {
                // Weight and oscillation share a scalar: 0..1 raise, small signed motion at the top.
                float raise = Mathf.Clamp01(Mathf.Abs(IntroWave));
                var at = new Vector3(.042f, .047f + .020f * raise, .024f);
                Hold(true, new Pose(at, Quaternion.Euler(0, 0, -18f + IntroWave * 16f)), transform);
                Hold(false, null);
            }
            else if (IntroWelcome > .0001f)
            {
                for (int side = -1; side <= 1; side += 2)
                    Hold(side > 0, new Pose(new Vector3(side * (.032f + .012f * IntroWelcome),
                        .044f + .010f * IntroWelcome, .025f),
                        Quaternion.Euler(-10f * IntroWelcome, 0, side * -22f * IntroWelcome)), transform);
            }
            else { Hold(true, null); Hold(false, null); }
        }
    }
}
