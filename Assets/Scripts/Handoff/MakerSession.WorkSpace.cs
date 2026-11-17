using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Pet = RoomCapsule.CapsulePetController;

namespace Windups.Handoff
{
    // (2026-11-12, after a Quest try: "Maker animates overlapping the other characters - they must not overlap".)
    // While Maker works (draws in its sketchbook, carves, paints, tosses a draft) it has a work space: a circle
    // round Maker (its body, mittens, tools and book: MakerWorkRoom), round where it is walking to, round the
    // work piece and round the paint pot. Maker picks a stand clear of the others first; anyone still inside
    // the work space (they were there, or walked in for a vote or a call-over) steps out to the nearest free,
    // visible spot on the same surface, at least the crew's minimum spacing from the others, and stays there.
    public sealed partial class MakerSession
    {
        public const float MakerWorkRoom = .13f, CrewBodyRadius = .045f;
        public int CrewMovedAside { get; private set; }
        public static bool KeepWorkSpace = true;

        private float nextWorkSpaceCheck;
        private readonly HashSet<Crew> steppingAside = new HashSet<Crew>();

        public bool MakerWorking => Sketching || Sculpt != SculptStage.None || tossing;

        // The circles Maker's work uses right now (empty when Maker is not working or is held).
        public List<(Vector3 at, float radius)> MakerWorkZones()
        {
            var zones = new List<(Vector3, float)>();
            if (!MakerWorking || !cast.TryGetValue(Crew.Maker, out var maker) || maker == null || maker.movement.IsHeld) return zones;
            zones.Add((maker.transform.position, MakerWorkRoom));
            if (maker.movement.State == Pet.MotionState.Walking) zones.Add((maker.movement.Destination, MakerWorkRoom));
            if (Sculpt != SculptStage.None)
            {
                float piece = blockHalf * 1.42f;
                if (view != null && view.gameObject.activeInHierarchy)
                {
                    var r = view.GetComponent<Renderer>();
                    if (r != null) piece = Mathf.Max(piece, Mathf.Max(r.bounds.extents.x, r.bounds.extents.z));
                }
                zones.Add((sculptSpot, piece + .02f));
            }
            if (pot != null && pot.gameObject.activeInHierarchy) zones.Add((pot.position, .035f));
            return zones;
        }

        // How far a crew member at p reaches into Maker's work space (0 = clear).
        private static float IntoZones(List<(Vector3 at, float radius)> zones, Vector3 p, float body)
        {
            float worst = 0f;
            foreach (var z in zones)
            {
                if (Mathf.Abs(z.at.y - p.y) > .2f) continue;
                float d = Vector3.ProjectOnPlane(p - z.at, Vector3.up).magnitude;
                worst = Mathf.Max(worst, z.radius + body - d);
            }
            return worst;
        }

        private void WorkSpaceFrame()
        {
            if (!KeepWorkSpace || Time.time < nextWorkSpaceCheck) return;
            nextWorkSpaceCheck = Time.time + .25f;
            var zones = MakerWorkZones();
            if (zones.Count == 0) return;
            foreach (var pair in cast)
            {
                var c = pair.Value;
                if (pair.Key == Crew.Maker || c == null || steppingAside.Contains(pair.Key)) continue;
                var pet = c.movement;
                if (pet.IsHeld || (pet.State != Pet.MotionState.Idle && pet.State != Pet.MotionState.Walking)) continue;
                if (IntoZones(zones, c.transform.position, CrewBodyRadius) <= .002f && (pet.State != Pet.MotionState.Walking || IntoZones(zones, pet.Destination, CrewBodyRadius) <= .002f)) continue;
                if (!SpotOutside(pair.Key, zones, out var spot))
                {
                    Debug.Log("[Windups Maker] " + pair.Key + " is in Maker's work space and has no free spot to step to");
                    continue;
                }
                StartCoroutine(StepAside(pair.Key, c, spot));
            }
        }

        // The nearest free spot out of the work space: same surface level, walkable from here, seen from the eyes,
        // and at least the crew's minimum spacing from everyone else (and from where others are stepping to).
        private bool SpotOutside(Crew who, List<(Vector3 at, float radius)> zones, out Vector3 spot)
        {
            spot = Vector3.zero;
            if (!cast.TryGetValue(who, out var c) || c == null) return false;
            var room = arrival.handoff.meeting.environment;
            Vector3 from = c.transform.position;
            var others = cast.Where(o => o.Key != who && o.Value != null).Select(o => o.Value.movement.State == Pet.MotionState.Walking ? o.Value.movement.Destination : o.Value.transform.position).ToList();
            // Away from the middle of the work space first.
            Vector3 middle = zones[0].at;
            Vector3 away = Vector3.ProjectOnPlane(from - middle, Vector3.up);
            away = away.sqrMagnitude > 1e-5f ? away.normalized : Vector3.ProjectOnPlane(Head != null ? Head.position - middle : Vector3.back, Vector3.up).normalized;
            float bestCost = float.MaxValue;
            // F51: first a spot that is also clear of every thing on the map (a made thing, a note, a flag, the mic,
            // v2's twin). On a full desk there may be none: then as before (clear of the work space and the others),
            // so Maker's work is never held up by it; the keeper tidies up afterwards where it can.
            var things = Footprints(false);
            foreach (bool strict in KeepClear ? new[] { true, false } : new[] { false })
            for (float r = .04f; r <= .45f; r += .03f)
            {
                for (int i = 0; i < 24; i++)
                {
                    Vector3 probe = from + Quaternion.Euler(0f, i * 15f, 0f) * away * r;
                    if (strict && !StandClear(who, probe, things)) continue;
                    if (!room.TrySameLevelNavPoint(probe, out var nav) || Mathf.Abs(nav.y - from.y) > .04f) continue;
                    if (IntoZones(zones, nav, CrewBodyRadius + .015f) > 0f) continue;
                    if (others.Any(o => Mathf.Abs(o.y - nav.y) < .2f && Vector3.ProjectOnPlane(o - nav, Vector3.up).magnitude < GatherFormation.Settings.MinSpacing)) continue;
                    if (Head != null && Physics.Linecast(Head.position, nav + Vector3.up * .12f, room.GeometryMask, QueryTriggerInteraction.Ignore)) continue;
                    // Short moves first, then toward the person rather than away.
                    float cost = Vector3.Distance(nav, from) + (Head != null ? .25f * Vector3.Distance(nav, Head.position) : 0f);
                    if (cost >= bestCost) continue;
                    if (!Walkable(from, nav, 1.2f)) continue;
                    bestCost = cost;
                    spot = nav;
                }
                if (bestCost < float.MaxValue) return true;
            }
            return false;
        }

        private IEnumerator StepAside(Crew who, WindupCharacter c, Vector3 spot)
        {
            steppingAside.Add(who);
            try
            {
                var pet = c.movement;
                pet.UnlockMeetingSeat();
                yield return null;
                CrewMovedAside++;
                if (pet.GoTo(spot, 1.5f, true))
                    for (float until = Time.time + 5f; Time.time < until && pet.State != Pet.MotionState.Idle && !pet.IsHeld; ) yield return null;
                for (int i = 0; i < 30 && !pet.MeetingSeatLocked && !pet.IsHeld; i++) { pet.LockMeetingSeat(); yield return null; }
                Debug.Log("[Windups Maker] " + who + " stepped out of Maker's work space to " + spot.ToString("F3"));
            }
            finally { steppingAside.Remove(who); }
        }

        // Tests: the deepest any teammate reaches into Maker's work space now (m), and who.
        public float WorkSpaceOverlap(out string who)
        {
            who = "";
            var zones = MakerWorkZones();
            float worst = 0f;
            foreach (var pair in cast)
            {
                if (pair.Key == Crew.Maker || pair.Value == null || pair.Value.movement.IsHeld) continue;
                float into = IntoZones(zones, pair.Value.transform.position, CrewBodyRadius);
                if (into > worst) { worst = into; who = pair.Key.ToString(); }
            }
            return worst;
        }
    }
}
