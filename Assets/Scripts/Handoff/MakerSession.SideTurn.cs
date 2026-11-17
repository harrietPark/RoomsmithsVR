using UnityEngine;

namespace Windups.Handoff
{
    // F88b (the user, 2026-11-15, after the horse came out 325 x 20 x 90 mm: fix the W/D axes first).
    // The size plan's W is the person's left-right (the size box's W line) and D front-back; the made model stands in the
    // box's turn, so its own x took W and its own z took D. Meshy turns a thing its own way: the horse's length ran along
    // its z, so "how wide: the full horse length" (325 mm) went on its thickness and "how deep" (20 mm) on its length.
    // Now, when the shape arrives (before it is shown), the first make of a thing is turned a quarter (SideQuarter) when
    // the person's long side and the model's long side disagree:
    //  - W and D both given: W >= D x SideMargin while the model's z >= x x SideMargin, or the other way round;
    //  - only W given: the model is long front to back (z >= x x LongThingRatio): the width the person showed is its length.
    // Turned, its z runs along the person's W: sizeD (z) takes W and sizeW (x) takes D (ApplyMeasuredSizes), the
    // carving view, MugTurn, approvedTurn and the round stand under the work turn with it (so later versions put on that
    // stand stand the same way), and CoasterDesign.sidesTurned keeps it for Fixer's words. v2's dimension lines, its
    // knobs' labels and size words ("wider", "2 cm deeper") follow the person's view (DraftAcross).
    public sealed partial class MakerSession
    {
        public static bool TurnToMeasuredSides = true;     // false: the model's x always takes W (before F88b)
        public const float SideMargin = 1.15f, LongThingRatio = 1.6f;
        public static readonly Quaternion SideQuarter = Quaternion.Euler(0f, -90f, 0f); // its front to the person's right
        private bool sidesTurnedNow;
        public int SideTurns { get; private set; }

        // Plain arithmetic (tests call it): turn the model so its long side runs along the person's W?
        // bounds: the model's own size (x, y, z); wMm / dMm: the person's width and depth (0 = not given).
        public static bool ShouldTurnSides(Vector3 bounds, float wMm, float dMm)
        {
            if (bounds.x < 1e-5f || bounds.z < 1e-5f) return false;
            float zOverX = bounds.z / bounds.x;
            if (wMm > 0f && dMm > 0f)
                return (wMm >= dMm * SideMargin && zOverX >= SideMargin) || (dMm >= wMm * SideMargin && zOverX <= 1f / SideMargin);
            if (wMm > 0f) return zOverX >= LongThingRatio;
            return false;
        }

        // Plan: the shape just came, before ShapeArrived shows it (the first make of a thing only; versions keep the turn
        // through the stand and the made thing).
        private void TurnToMeasuredSidesNow(Mesh source)
        {
            sidesTurnedNow = false;
            // (A later version - even one whose earlier version went into the notebook - stands on the already turned stand.)
            if (!TurnToMeasuredSides || source == null || Product != null || draftFrom != null || Versions.Count > 0) return;
            float w = MeasuredAxis("W"), d = MeasuredAxis("D");
            var b = source.bounds.size;
            if (!ShouldTurnSides(b, w, d)) return;
            sidesTurnedNow = true;
            SideTurns++;
            sculptTurn *= SideQuarter;
            MugTurn *= SideQuarter;
            approvedTurn *= SideQuarter;
            var stand = pendingStand != null ? pendingStand : projectPlate;
            if (stand != null) stand.rotation *= SideQuarter; // (round: only what later versions take from it changes)
            // F89: a stand in the person's hand is re-posed from the grab every frame (Hold: holdTurn; CarryWork: the turns
            // captured by PickWork), which would undo the quarter at once: the grab takes the turned state as its own.
            if (stand != null && held == stand) { holdTurn *= SideQuarter; standCarryTurn *= SideQuarter; } // (F97: the carried stand's own turn)
            if (workCarried && workStand != null) PickWork(workStand);
            Talk.Add("Maker turned the model so its long side runs the way the person measured its width.");
            WriteInputLog($"sides turned | W {w:0} D {d:0} mm | model {b.x:F2} x {b.z:F2} (x by z) | its long side now along W", true);
        }

        // The person's width and depth of a model design (mm): its own x and z, swapped when it was turned.
        public static float PersonWidthMm(CoasterDesign d) => d == null ? 0f : d.sidesTurned ? (d.sizeD > 0f ? d.sizeD : d.cupDiameter) : (d.sizeW > 0f ? d.sizeW : d.cupDiameter);
        public static float PersonDepthMm(CoasterDesign d) => d == null ? 0f : d.sidesTurned ? d.sizeW : d.sizeD;

        // v2 (the draft twin) seen side-on: its own x runs front to back as the person sees it, so W and D swap.
        private bool DraftAcross
        {
            get
            {
                if (draftMesh == null || Head == null || DraftIsClip) return false;
                Vector3 toUser = Vector3.ProjectOnPlane(Head.position - draftMesh.position, Vector3.up);
                if (toUser.sqrMagnitude < 1e-6f) return false;
                Vector3 right = Vector3.Cross(Vector3.up, toUser.normalized);
                return Mathf.Abs(Vector3.Dot(draftMesh.right, right)) < Mathf.Abs(Vector3.Dot(draftMesh.forward, right));
            }
        }

        // The draft's own axis (0 x, 1 y, 2 z) for the person's W (0), H (1) or D (2), and back (the same swap).
        private int DraftSide(int axis) => axis >= 0 && axis != 1 && DraftAcross ? 2 - axis : axis;
    }
}
