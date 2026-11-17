using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // One Hand per side. The interaction rig carries several Hand components for each
    // physical hand (raw data source, synthetic hands, interactor groups); treating each as a
    // separate hand counted one pinch several times (for example one far pinch grabbing a card
    // with several pointers, which reads as a two-hand stretch or crumple). Prefer the raw OVR
    // data source; never touch inactive hands (some assert when queried).
    public static class HandSet
    {
        private static Hand left, right;
        private static int frame = -1;
        private static readonly List<Hand> both = new List<Hand>(2);

        public static IReadOnlyList<Hand> Both()
        {
            if (frame != Time.frameCount)
            {
                frame = Time.frameCount;
                if (left == null || !left.isActiveAndEnabled) left = Pick(Handedness.Left);
                if (right == null || !right.isActiveAndEnabled) right = Pick(Handedness.Right);
                both.Clear();
                if (left != null) both.Add(left);
                if (right != null) both.Add(right);
            }
            return both;
        }

        public static Hand Side(Handedness side)
        {
            Both();
            return side == Handedness.Left ? left : right;
        }

        private static Hand Pick(Handedness side)
        {
            Hand fallback = null;
            foreach (var hand in Object.FindObjectsByType<Hand>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (hand == null || !hand.isActiveAndEnabled || hand.Handedness != side) continue;
                if (hand.name.Contains("OVRHandDataSource")) return hand;
                if (fallback == null) fallback = hand;
            }
            return fallback;
        }
    }
}
