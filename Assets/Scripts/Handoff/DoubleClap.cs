using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // Counts a fast palm approach only after the hands have separated. Tuned to be forgiving:
    // tracking confidence and palm alignment drop as the palms meet, and the closest frames
    // are often missed, so the windows are wide and the contact distance generous.
    public sealed class DoubleClap
    {
        private bool separated;
        private float previousDistance = -1f;
        private float firstClap = float.NegativeInfinity;
        private float previousSample = float.NegativeInfinity;
        private float fastApproach = float.NegativeInfinity;
        private float rearmDistance = .14f;
        public float FirstContactTime => firstClap;
        // How many claps the gesture needs (the arrival uses a single clap).
        public int Required { get; set; } = 2;
        private float alignedAt = float.NegativeInfinity;
        private float reliableAt = float.NegativeInfinity;

        public void Reset()
        {
            separated = false;
            previousDistance = -1f;
            firstClap = float.NegativeInfinity;
            previousSample = fastApproach = float.NegativeInfinity;
            rearmDistance = .14f;
            alignedAt = reliableAt = float.NegativeInfinity;
        }

        public bool Observe(float distance, bool palmsAligned, float now, bool highConfidence = true)
        {
            float elapsed = now - previousSample;
            if (elapsed > .3f)
            {
                separated = false;
                fastApproach = float.NegativeInfinity;
                rearmDistance = .14f;
            }
            if (highConfidence) reliableAt = now;
            if (palmsAligned && highConfidence) alignedAt = now;
            if (distance >= rearmDistance && now - reliableAt <= .3f) separated = true;
            if (separated && now - alignedAt <= .3f && now - reliableAt <= .3f &&
                distance < .3f && elapsed > 0f && elapsed <= .3f &&
                (previousDistance - distance) / elapsed >= .3f)
                fastApproach = now;
            // A fast, aligned approach can briefly lose confidence or miss the closest
            // frame as the palms overlap. A visible rebound can confirm that motion.
            bool recentApproach = separated && now - fastApproach <= .3f;
            bool contact = recentApproach && distance <= .15f;
            bool rebound = recentApproach && highConfidence && previousDistance <= .2f &&
                           distance - previousDistance >= .025f;
            float closest = rebound ? previousDistance : distance;
            previousSample = now;
            previousDistance = distance;
            if (!contact && !rebound) return false;
            rearmDistance = Mathf.Clamp(closest + .025f, .1f, .14f);
            separated = rebound && distance >= rearmDistance;
            fastApproach = float.NegativeInfinity;
            return CountContact(now);
        }

        public bool CountContact(float now)
        {
            if (Required <= 1)
            {
                Reset();
                return true;
            }
            float interval = now - firstClap;
            if (interval < .085f) return false;
            if (interval <= 1.6f)
            {
                Reset();
                return true;
            }
            firstClap = now;
            return false;
        }

        public static bool TryPalm(Hand hand, out Vector3 center, out Vector3 normal)
        {
            center = normal = Vector3.zero;
            if (!hand.IsTrackedDataValid ||
                !hand.GetJointPose(HandJointId.HandWristRoot, out Pose wrist) ||
                !hand.GetJointPose(HandJointId.HandIndex1, out Pose index) ||
                !hand.GetJointPose(HandJointId.HandPinky1, out Pose pinky)) return false;

            center = (wrist.position + index.position + pinky.position) / 3f;
            normal = Vector3.Cross(index.position - wrist.position, pinky.position - wrist.position).normalized;
            return normal.sqrMagnitude > .5f;
        }
    }
}
