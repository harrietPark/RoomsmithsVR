using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    // F105 (Quest 2026-11-16, the user: "every object I make leaves a plate. When a coaster is finished its plate should
    // go, and when I make another one a plate comes again with its mesh - one plate in the app"; "the coaster stays where
    // it is"; "and when things come back, no plate"):
    //  - The display stand (F52) belongs to the work in progress only. When the work is finished (ending Archived, before
    //    Fixer's Export question) every stand goes: what stood on it comes down onto the surface the stand stood on, right
    //    where it was, and its place is saved without a stand.
    //  - PutAside (Make another, start over, a fresh note) clears any stand that is left (an unfinished make's too).
    //  - Coming back, nothing comes back on a stand: a thing saved on one (older saves) comes back where the stand stood,
    //    lowered by the stand's height; moved, it is saved without one.
    //  - Editing a finished work (notebook Edit, ReopenWork) puts one stand under it again (its < vN > pager needs it); any
    //    other stand goes first.
    // OneStand = false keeps the F52/F104 stands (VerifyF104 sets it for its restore checks).
    public sealed partial class MakerSession
    {
        public static bool OneStand = true;
        public int StandsRetired { get; private set; }
        public int RestoredWithoutStand { get; private set; }

        private readonly List<Transform> retireRiders = new List<Transform>();

        // Every stand goes except `keep`; what stood on each comes down onto the surface under it.
        private void RetireStands(Transform keep = null)
        {
            if (!OneStand || Stands.Count == 0) return;
            foreach (var stand in new List<Transform>(Stands))
            {
                if (stand == null) { Stands.Remove(stand); continue; }
                if (stand == keep) continue;
                Vector3 top = StandTopOf(stand);
                Vector3 drop = stand.position - top; // (the stand's height, along its up)
                float R = StandRadiusOf(stand);
                retireRiders.Clear();
                foreach (var t in MadeThings()) AddRider(t);
                AddRider(Product);
                AddRider(Kept);
                foreach (var v in Versions) AddRider(v?.root);
                foreach (var t in RoomItems) AddRider(t);
                if (held != null && retireRiders.Contains(held)) retireRiders.Remove(held);
                if (held == stand) { holdHand = null; mouseHolding = false; held = null; heldBody = null; }
                foreach (var t in retireRiders)
                {
                    t.position += drop;
                    SyncBody(t);
                }
                StandsRetired++;
                WriteInputLog("stand gone (F105, one stand in the app): " + retireRiders.Count + " thing(s) set down where it stood", true);
                DestroyStand(stand);
                ToySounds.Play(ToySounds.Cue.Pop, top, .35f, 1.5f);
                foreach (var t in retireRiders) SaveSpotNow(t);

                void AddRider(Transform t)
                {
                    if (t == null || IsStand(t) || retireRiders.Contains(t)) return;
                    if (StandsOn(t, stand)) { retireRiders.Add(t); return; }
                    // (a version put away behind the stand's < > is inactive or not drawn: its pivot is its bottom)
                    Vector3 p = t.position;
                    if (Mathf.Abs(p.y - top.y) < .02f && new Vector2(p.x - top.x, p.z - top.z).magnitude <= R) retireRiders.Add(t);
                }
            }
        }

        // The finished work: its stand goes (Ending, right after it is archived).
        private void RetireStandsAfterFinish()
        {
            if (!OneStand) return;
            RetireStands();
        }

        private void SaveSpotNow(Transform thing)
        {
            if (thing == null || !thing.gameObject.activeInHierarchy) return;
            if (thing == Product) { if (productFinished) Remember(); } // (an unfinished make is not saved)
            else if (madeNames.TryGetValue(thing, out var name)) RememberPlace(name, thing);
        }

        // Coming back: a thing saved on a stand comes back where that stand stood (its height taken off), with no stand.
        private bool RestoreWithoutStand(Transform made, float radius, float[] turn)
        {
            if (!OneStand) return false;
            if (made == null || radius <= 0f) return true;
            Quaternion standTurn = turn != null && turn.Length == 4 ? made.rotation * new Quaternion(turn[0], turn[1], turn[2], turn[3]) : Quaternion.identity;
            made.position -= standTurn * Vector3.up * StandTop;
            SyncBody(made);
            RestoredWithoutStand++;
            return true;
        }

        // Editing a finished work: one stand under it again (the others go).
        private Transform StandForReopened(Transform thing)
        {
            var keep = StandUnder(thing);
            if (!OneStand) return keep;
            RetireStands(keep);
            if (keep != null || thing == null) return keep;
            var r = thing.GetComponentInChildren<MeshRenderer>();
            if (r == null) return null;
            var b = r.bounds;
            float modelRadius = Mathf.Max(b.extents.x, b.extents.z);
            var stand = MakeStand(new Vector3(b.center.x, b.min.y, b.center.z), Upright(thing.rotation), modelRadius);
            thing.position += Vector3.up * StandTop;
            SyncBody(thing);
            return stand;
        }
    }
}
