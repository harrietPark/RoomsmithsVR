using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Pet = RoomCapsule.CapsulePetController;

namespace Windups.Handoff
{
    // Put down away from the others (2026-11-11, after the Quest tries): a character the person carried off and
    // dropped away from the meeting (say on the floor while the others sit on the desk), or that ended up there
    // some other way, goes back (never teleported; jumping up furniture as it can) to a free seat on the
    // meeting's surface - its own seat if free, else a spot that keeps everyone CrewSpacing apart - and sits
    // there again. A route that fails (blocked, fell off it) is avoided and another one is tried, up to
    // MaxReturnTries. If there is no way back at all it goes as near as it can, raises its hand and asks to be
    // lifted, again every so often, until someone carries it.
    public sealed partial class MakerSession
    {
        public const float FarFromPerson = 1.3f, FarFromFriends = .5f;
        public const int MaxReturnTries = 4;
        public const float AskAgainEvery = 9f;
        public int WalkBacks { get; private set; }
        public int Strandings { get; private set; }
        public int ReturnRetries { get; private set; }
        public int StrandedAsks { get; private set; }

        private readonly Dictionary<Pet, int> seenGrabs = new Dictionary<Pet, int>();
        private readonly Dictionary<Pet, float> settledSince = new Dictionary<Pet, float>();
        private readonly Dictionary<Pet, float> nextAwayCheck = new Dictionary<Pet, float>();
        private readonly HashSet<Pet> dropped = new HashSet<Pet>(), walkingBack = new HashSet<Pet>(), stranded = new HashSet<Pet>();

        private void ReturnFrame()
        {
            if (!Begun || Head == null) return;
            TeleportBackFrame(); // F82: away for a minute: back to its seat at once
            foreach (var pair in cast)
            {
                var pet = pair.Value != null ? pair.Value.movement : null;
                if (pet == null) continue;
                if (!seenGrabs.TryGetValue(pet, out int grabs)) { seenGrabs[pet] = pet.GrabCount; continue; }
                if (pet.GrabCount != grabs) { seenGrabs[pet] = pet.GrabCount; dropped.Add(pet); settledSince.Remove(pet); stranded.Remove(pet); }
                if (walkingBack.Contains(pet) || stranded.Contains(pet)) continue;
                bool settled = !pet.IsHeld && (pet.MeetingSeatLocked || pet.State == Pet.MotionState.Idle);
                if (!settled) { settledSince.Remove(pet); continue; }
                if (!settledSince.TryGetValue(pet, out float since)) { settledSince[pet] = Time.time; continue; }
                if (dropped.Contains(pet))
                {
                    if (Time.time - since < 1.2f) continue;
                    dropped.Remove(pet);
                    settledSince.Remove(pet);
                    if (IsAway(pet)) StartCoroutine(WalkBack(pair.Key, pet));
                    continue;
                }
                // Not carried, but away all the same (fell off while walking, a vote or a tour ended badly):
                // checked now and then once it has stood still a while, unless it is away on purpose.
                // Only being apart from the meeting counts here (not the person having walked off: then they
                // stay where the person left them, and a clap calls them over).
                if (Time.time - since < 3f || AwayOnPurpose(pair.Key)) continue;
                if (nextAwayCheck.TryGetValue(pet, out float next) && Time.time < next) continue;
                nextAwayCheck[pet] = Time.time + 4f;
                if (IsAway(pet, false)) StartCoroutine(WalkBack(pair.Key, pet));
            }
        }

        private bool AwayOnPurpose(Crew crew) => Comparing || (crew == Crew.Spark && SparkWalking) || (crew == Crew.Maker && Sculpt != SculptStage.None) || AskingAway(crew); // F76

        private static float Flat(Vector3 a, Vector3 b) => Vector3.ProjectOnPlane(a - b, Vector3.up).magnitude;

        // The meeting's level: the seats the crew gathered on (a desk), when known.
        private bool MeetingLevel(out float y)
        {
            var seats = arrival != null && arrival.Formation != null ? arrival.Formation.Seats : null;
            y = 0f;
            if (seats == null || seats.Length == 0) return false;
            y = seats.Average(s => s.y);
            return true;
        }

        private IEnumerable<Pet> Others(Pet pet) => cast.Values.Where(c => c != null && c.movement != null && c.movement != pet).Select(c => c.movement);

        private bool IsAway(Pet pet, bool fromPerson = true)
        {
            Vector3 at = pet.transform.position;
            if (fromPerson && Flat(at, Head.position) > FarFromPerson) return true;
            if (MeetingLevel(out float level))
            {
                // On another level than the meeting (the floor under the desk) is away, whatever the friends do.
                if (Mathf.Abs(at.y - level) > .08f) return true;
                var onLevel = Others(pet).Where(o => Mathf.Abs(o.transform.position.y - level) < .08f).Select(o => o.transform.position).ToList();
                return onLevel.Count > 0 && onLevel.Min(f => Vector3.Distance(f, at)) > FarFromFriends;
            }
            var friends = Others(pet).Select(o => o.transform.position).ToList();
            return friends.Count > 0 && friends.Min(f => Vector3.Distance(f, at)) > FarFromFriends;
        }

        // Where to go back to: its own (nearest free) meeting seat, else beside the nearest friend on the
        // meeting level; either way kept roomy next to the others.
        private bool ReturnSpot(Pet pet, out Vector3 goal)
        {
            var room = arrival.handoff.meeting.environment;
            var others = Others(pet).Select(o => o.transform.position).ToList();
            Vector3 want;
            var seats = arrival.Formation != null ? arrival.Formation.Seats : null;
            var freeSeats = seats == null ? new List<Vector3>() : seats.Where(s => others.All(o => Vector3.Distance(o, s) > GatherFormation.CrewSpacing * .8f)).ToList();
            if (freeSeats.Count > 0) want = freeSeats.OrderBy(s => Vector3.Distance(s, pet.transform.position)).First();
            else
            {
                var friend = Others(pet).Where(o => !walkingBack.Contains(o) && !stranded.Contains(o))
                    .OrderBy(o => Vector3.Distance(o.transform.position, pet.transform.position)).FirstOrDefault();
                if (friend != null)
                {
                    Vector3 f = friend.transform.position;
                    Vector3 toPerson = Vector3.ProjectOnPlane(Head.position - f, Vector3.up).normalized;
                    Vector3 side = Vector3.Cross(Vector3.up, toPerson);
                    if (Vector3.Dot(pet.transform.position - f, side) < 0f) side = -side;
                    want = f + side * (GatherFormation.CrewSpacing + .02f);
                }
                else want = DeskPointInFront();
            }
            // F51: clear of the things on the desk too (a seat may have a made thing or a note on it by now).
            if (CrewOf(pet, out var who) && FreeStand(who, want, .32f, out goal, null, false)) return true;
            if (GatherFormation.FreeSpotNear(room, Head.position, want, others, .32f, out goal)) return true;
            return room.TrySameLevelNavPoint(want, out goal);
        }

        private IEnumerator WalkBack(Crew crew, Pet pet)
        {
            walkingBack.Add(pet);
            var room = arrival.handoff.meeting.environment;
            int grabs = pet.GrabCount;
            pet.UnlockMeetingSeat();
            yield return null;
            yield return new WaitForSeconds(.3f);
            bool back = false, started = false;
            Vector3 goal = pet.transform.position;
            teleportedPets.Remove(pet);
            for (int attempt = 0; attempt < MaxReturnTries && !pet.IsHeld && pet.GrabCount == grabs && !teleportedPets.Contains(pet); attempt++)
            {
                // Wait for it to stand (a landing takes half a second).
                for (float until = Time.time + 3f; Time.time < until && pet.State != Pet.MotionState.Idle && !pet.IsHeld; ) yield return null;
                if (pet.IsHeld || pet.State != Pet.MotionState.Idle) break;
                if (!ReturnSpot(pet, out goal)) break;
                // Already where it would go: nothing to do (and nothing to say).
                if (Vector3.Distance(goal, pet.transform.position) < .1f) { back = true; break; }
                int failures = pet.TraversalFailures;
                bool going = pet.GoTo(goal, 12f);
                Debug.Log($"[Windups Maker] {MakerRoles.Name(crew)} walks back (try {attempt + 1}): from {pet.transform.position:F2} to {goal:F2} going {going}");
                if (!going) break;
                if (!started)
                {
                    started = true;
                    WalkBacks++;
                    Caption(crew, "Coming back!", 2f);
                    Talk.Add(MakerRoles.Name(crew) + " went back to the others");
                }
                else ReturnRetries++;
                // On its way: walking, jumping up, hopping down and landing (the controller keeps going to
                // the goal after each route), until it stops.
                float deadline = Time.time + 40f;
                while (Time.time < deadline && !pet.IsHeld && pet.GrabCount == grabs && pet.State != Pet.MotionState.Idle) yield return null;
                if (pet.IsHeld || pet.GrabCount != grabs) break;
                if (Vector3.Distance(pet.transform.position, goal) < .08f) { back = true; break; }
                // Stopped short: the route it last used failed (or led the wrong way); avoid it and try again.
                if (pet.LastTraversal != null && (pet.TraversalFailures != failures || Mathf.Abs(pet.transform.position.y - goal.y) > .06f))
                    room.Avoid(pet.LastTraversal, 40f);
            }
            if (teleportedPets.Contains(pet)) { walkingBack.Remove(pet); yield break; } // F82
            if (!pet.IsHeld && pet.GrabCount == grabs)
            {
                if (back || !IsAway(pet))
                    for (int i = 0; i < 60 && !pet.MeetingSeatLocked && !pet.IsHeld; i++) { pet.LockMeetingSeat(); yield return null; }
                else
                {
                    walkingBack.Remove(pet);
                    stranded.Add(pet);
                    StartCoroutine(AskToBeLifted(crew, pet, grabs));
                    yield break;
                }
            }
            seenGrabs[pet] = pet.GrabCount;
            walkingBack.Remove(pet);
        }

        // No way back: go as near the others as its own level allows, then raise a hand and ask to be lifted,
        // again every AskAgainEvery seconds (when nobody is talking), until it is carried.
        private IEnumerator AskToBeLifted(Crew crew, Pet pet, int grabs)
        {
            Strandings++;
            var room = arrival.handoff.meeting.environment;
            var others = Others(pet).Select(o => o.transform.position).ToList();
            if (others.Count > 0)
            {
                Vector3 centre = others.Aggregate(Vector3.zero, (a, b) => a + b) / others.Count;
                Vector3 below = new Vector3(centre.x, pet.transform.position.y, centre.z);
                Vector3 toward = Vector3.MoveTowards(pet.transform.position, below, Mathf.Max(0f, Flat(pet.transform.position, below) - .3f));
                if (room.TrySameLevelNavPoint(toward, out var near) && Vector3.Distance(near, pet.transform.position) > .1f && pet.GoTo(near, 3f))
                    for (float until = Time.time + 12f; Time.time < until && pet.State != Pet.MotionState.Idle && !pet.IsHeld; ) yield return null;
            }
            for (int i = 0; i < 30 && !pet.MeetingSeatLocked && !pet.IsHeld; i++) { pet.LockMeetingSeat(); yield return null; }
            float next = 0f;
            while (!pet.IsHeld && pet.GrabCount == grabs && Begun && !teleportedPets.Contains(pet))
            {
                if (Time.time >= next && lines.Count == 0 && !Voice.IsSpeaking && !WindupBody.UserTalking)
                {
                    StrandedAsks++;
                    Body(crew)?.Play(StrandedAsks % 2 == 1 ? "HandRaise" : "Wave");
                    Caption(crew, StrandedAsks == 1 ? "I can't jump up from here. Lift me up?" : "Up here, please?", 3.5f);
                    next = Time.time + AskAgainEvery;
                }
                yield return null;
            }
            stranded.Remove(pet);
        }

        public bool WalkingBack(Crew crew) => cast.TryGetValue(crew, out var c) && c != null && walkingBack.Contains(c.movement);
        public bool Stranded(Crew crew) => cast.TryGetValue(crew, out var c) && c != null && stranded.Contains(c.movement);

        // Tests: a character put down at `at` as if carried there (it then decides whether to walk back).
        public bool SimulateDropFar(Crew crew, Vector3 at)
        {
            if (!cast.TryGetValue(crew, out var c) || c == null) return false;
            var pet = c.movement;
            pet.UnlockMeetingSeat();
            bool placed = pet.TryPlaceAt(at);
            seenGrabs[pet] = pet.GrabCount;
            dropped.Add(pet);
            settledSince.Remove(pet);
            stranded.Remove(pet);
            return placed;
        }

        public Transform CrewMember(Crew crew) => cast.TryGetValue(crew, out var c) && c != null ? c.movement.transform : null;
        public void SimulateStep(Step step) => Current = step;
    }
}
