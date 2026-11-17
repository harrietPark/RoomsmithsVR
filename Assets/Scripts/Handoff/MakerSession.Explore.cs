using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Windups.Handoff
{
    // "Spark, can you find anything interesting in my room?" (2026-11-11): Spark looks (through the person's
    // camera), then trots from thing to thing it found - up to three, the ones its ideas are for first -
    // and at each one its name tag hops and Spark peeks at it, collecting an idea; the name tags stay up
    // while the idea cards come out, then Spark goes back to its seat.
    public sealed partial class MakerSession
    {
        public int TourStops { get; private set; }

        private IEnumerator Tour(List<RoomEyes.Found> stops)
        {
            if (!cast.TryGetValue(Crew.Spark, out var spark) || spark == null || stops == null || stops.Count == 0) yield break;
            var pet = spark.movement;
            Vector3 home = pet.transform.position;
            var room = arrival.handoff.meeting.environment;
            SparkWalking = true;
            seenUntil = Mathf.Max(seenUntil, Time.time + 30f);
            pet.UnlockMeetingSeat();
            yield return null;
            foreach (var stop in stops.Take(3))
            {
                Vector3 target = stop.basePoint;
                // Only along the surface Spark stands on, a short way (no climbing or crossing the room on the
                // room model's boxes, which can be bigger than the real furniture): else it looks from here.
                if (!WalkableNear(pet, target, out Vector3 goalHere))
                {
                    TourStops++;
                    MarkStop(stop);
                    focus.position = target;
                    spark.lookTarget = focus;
                    ToySounds.Play(ToySounds.Cue.Pop, target, .35f, 1.6f);
                    WindupBody.Of(spark)?.Play("Peek");
                    Talk.Add("Spark looked at the " + stop.seen.name + " from its seat");
                    yield return new WaitForSeconds(.8f);
                    continue;
                }
                Vector3 goal = goalHere;
                focus.position = target;
                spark.lookTarget = null;
                bool going = Vector3.Distance(goal, pet.transform.position) > .08f && pet.GoTo(goal, 2.5f);
                float until = Time.time + 6f;
                while (going && Time.time < until && pet.State == RoomCapsule.CapsulePetController.MotionState.Walking && Vector3.Distance(pet.transform.position, goal) > .05f)
                {
                    SparkReach = Mathf.Max(SparkReach, Vector3.Distance(pet.transform.position, home));
                    yield return null;
                }
                // There: its name tag hops, Spark peeks, a sparkle pops over it.
                spark.lookTarget = focus;
                TourStops++;
                MarkStop(stop);
                ToySounds.Play(ToySounds.Cue.Pop, target, .45f, 1.6f);
                ToyFx.Confetti(stop.top + Vector3.up * .03f, 6);
                var body = WindupBody.Of(spark);
                if (body != null) body.Play("Peek"); else spark.SetCue("encourage");
                Talk.Add("Spark visited the " + stop.seen.name);
                yield return new WaitForSeconds(.8f);
                spark.SetCue("idle");
            }
            home = ClearSeat(Crew.Spark, home); // F51: its seat may have filled up meanwhile
            bool back = pet.GoTo(home, 4f);
            float deadline = Time.time + 8f;
            while (back && Time.time < deadline && pet.State == RoomCapsule.CapsulePetController.MotionState.Walking && Vector3.Distance(pet.transform.position, home) > .05f) yield return null;
            for (int i = 0; i < 60 && !pet.MeetingSeatLocked; i++) { pet.LockMeetingSeat(); yield return null; }
            spark.lookTarget = null;
            SparkWalking = false;
        }

        public const float TourReach = 1.6f;

        // A spot toward `target` that Spark reaches by walking on the level it stands on (no climbing up or down
        // furniture, at most TourReach of path): beside the thing when it stands on the same surface, else as
        // near as that level goes (the floor at the desk's edge) - and at least 10 cm nearer than now.
        private bool WalkableNear(RoomCapsule.CapsulePetController pet, Vector3 target, out Vector3 goal)
        {
            goal = target;
            var room = arrival.handoff.meeting.environment;
            Vector3 from = pet.transform.position;
            Vector3 level = new Vector3(target.x, from.y, target.z);
            Vector3 away = Vector3.ProjectOnPlane(from - target, Vector3.up);
            Vector3 near = level + (away.sqrMagnitude > .0001f ? away.normalized : Vector3.forward) * .08f;
            var path = new UnityEngine.AI.NavMeshPath();
            Vector3 end = from;
            bool found = false;
            // Under a desk the floor is not walkable: step back toward Spark until it is.
            for (float t = 0f; t <= .9f && !found; t += .1f)
            {
                Vector3 probe = Vector3.Lerp(near, from, t);
                if (room.TrySameLevelNavPoint(probe, out var snapped) && Mathf.Abs(snapped.y - from.y) < .06f &&
                    UnityEngine.AI.NavMesh.CalculatePath(from, snapped, room.Query, path) && path.status != UnityEngine.AI.NavMeshPathStatus.PathInvalid && path.corners.Length > 1)
                {
                    end = path.corners[path.corners.Length - 1];
                    found = true;
                }
            }
            if (!found) { Debug.Log($"[Windups Maker] Spark stays: no path on its level from {from:F2} toward {target:F2}"); return false; }
            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++)
            {
                if (Mathf.Abs(path.corners[i].y - from.y) > .06f) { Debug.Log($"[Windups Maker] Spark stays: the way climbs ({path.corners[i]:F2})"); return false; }
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                if (length > TourReach) { Debug.Log($"[Windups Maker] Spark stays: too far ({length:F2} m)"); return false; }
            }
            // F51: beside the thing, never in it (nor in a friend, a made thing, a note or the mic).
            if (KeepClear && CrewOf(pet, out var who) && FreeStand(who, end, .2f, out var free, null, false)) end = free;
            float before = Vector3.ProjectOnPlane(from - target, Vector3.up).magnitude, after = Vector3.ProjectOnPlane(end - target, Vector3.up).magnitude;
            if (before - after < .1f) { Debug.Log($"[Windups Maker] Spark stays: no nearer ({before:F2} -> {after:F2} m, path {path.status})"); return false; }
            goal = end;
            return true;
        }

        // The visited thing's name tag hops and stays a little bigger.
        private void MarkStop(RoomEyes.Found stop)
        {
            foreach (var mark in seenMarks)
                if (mark.found == stop && mark.label != null) StartCoroutine(Pulse(mark.label));
        }

        private static IEnumerator Pulse(Transform ring)
        {
            if (ring == null) yield break;
            Vector3 size = ring.localScale;
            for (float t = 0f; t < 1f && ring != null; t += Time.deltaTime / .5f)
            {
                ring.localScale = size * (1f + .25f * Mathf.Sin(t * Mathf.PI));
                yield return null;
            }
            if (ring != null) ring.localScale = size * 1.08f;
        }

        // "Find something interesting": before a design exists Spark suggests what to make (with the tour);
        // later it explores without changing the step and offers what it found as inspiration.
        private void LookAroundNow()
        {
            if (Eyes == null || Eyes.Looking || SparkWalking || looking) return;
            if (Current == Step.Suggesting || Current == Step.Style) StartCoroutine(Suggest());
            else StartCoroutine(Explore());
        }

        private IEnumerator Explore()
        {
            Ack(Crew.Spark, "Ooh, let me look around!");
            List<RoomEyes.Found> seen = null;
            yield return SparkLooks("interesting things around the person that could inspire what we make: colours, textures, shapes", list => seen = list);
            if (seen == null || seen.Count == 0) { Say(Crew.Spark, Eyes.LastError ?? "Hmm, I can't see much from here. Turn around with me?"); yield break; }
            Talk.Add("Spark explored and found: " + string.Join(", ", seen.Select(f => f.seen.name)));
            yield return Tour(TourStopsFor(seen, new string[0]));
            var names = seen.Take(3).Select(f => MakerRoles.Clean(f.seen.name)).ToList();
            Say(Crew.Spark, "I found your " + Join(names) + "! Want to grab a colour or texture from one?");
        }

        // Where Spark goes on its tour: the things its ideas are for, then other things it saw (on surfaces).
        private static List<RoomEyes.Found> TourStopsFor(List<RoomEyes.Found> seen, IEnumerable<string> wanted)
        {
            var stops = new List<RoomEyes.Found>();
            if (seen == null) return stops;
            foreach (var name in wanted)
            {
                var f = Locate(name, seen);
                if (f != null && f.onSurface && !stops.Contains(f)) stops.Add(f);
            }
            foreach (var f in seen) if (f.onSurface && !stops.Contains(f)) stops.Add(f);
            return stops.Take(3).ToList();
        }
    }
}
