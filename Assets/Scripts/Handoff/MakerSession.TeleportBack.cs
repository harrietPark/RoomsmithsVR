using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Pet = RoomCapsule.CapsulePetController;

namespace Windups.Handoff
{
    // F82 (2026-11-14, the user after the Quest try: "while I was measuring, Fixer went far away and kept failing to come
    // back to me - if it can't come back beside me for about a minute, just teleport it to its place"). The walk back
    // (MakerSession.Return.cs) still comes first; a teammate that has been away from the meeting for TeleportBackAfter
    // (not carried, not away on purpose) pops out where it is and pops in at its free meeting seat. This overrides the
    // "never teleport" rule for this one case only.
    public sealed partial class MakerSession
    {
        public static float TeleportBackAfter = 60f;
        public int TeleportsBack { get; private set; }
        private readonly Dictionary<Pet, float> awaySince = new Dictionary<Pet, float>();
        private readonly HashSet<Pet> teleportedPets = new HashSet<Pet>();
        private float nextTeleportCheck;

        private void TeleportBackFrame()
        {
            if (!Begun || Head == null || Time.time < nextTeleportCheck) return;
            nextTeleportCheck = Time.time + .5f;
            foreach (var pair in cast)
            {
                var pet = pair.Value != null ? pair.Value.movement : null;
                if (pet == null) continue;
                bool away = !pet.IsHeld && !AwayOnPurpose(pair.Key) && Sculpt == SculptStage.None && IsAway(pet);
                if (!away) { awaySince.Remove(pet); continue; }
                if (!awaySince.TryGetValue(pet, out float since)) { awaySince[pet] = Time.time; continue; }
                if (Time.time - since < TeleportBackAfter) continue;
                awaySince.Remove(pet);
                TeleportBack(pair.Key, pet);
            }
        }

        private void TeleportBack(Crew crew, Pet pet)
        {
            if (!ReturnSpot(pet, out var goal)) return;
            Vector3 from = pet.transform.position;
            teleportedPets.Add(pet); // a walk back or a "lift me" still running for it stops
            walkingBack.Remove(pet);
            stranded.Remove(pet);
            pet.StopWalking();
            pet.UnlockMeetingSeat();
            if (!pet.TryPlaceAt(goal)) return;
            TeleportsBack++;
            ToyFx.Puff(from + Vector3.up * .05f, .02f, 6);
            ToyFx.Puff(goal + Vector3.up * .05f, .02f, 6);
            ToySounds.Play(ToySounds.Cue.Pop, goal, .5f, 1.2f);
            Body(crew)?.Play("Land");
            WriteInputLog("teleport back | " + MakerRoles.Name(crew) + " was away " + TeleportBackAfter.ToString("0") + " s: " + from.ToString("F2") + " -> " + goal.ToString("F2"), true);
            StartCoroutine(SeatAfterTeleport(pet));
        }

        private IEnumerator SeatAfterTeleport(Pet pet)
        {
            yield return new WaitForSeconds(.2f);
            for (int i = 0; i < 60 && pet != null && !pet.MeetingSeatLocked && !pet.IsHeld; i++) { pet.LockMeetingSeat(); yield return null; }
            yield return new WaitForSeconds(1f);
            if (pet != null) teleportedPets.Remove(pet);
        }

        // Checks: put a teammate away and pretend the minute passed.
        public void SimulateAwayFor(Crew crew, float seconds)
        {
            if (cast.TryGetValue(crew, out var c) && c != null) awaySince[c.movement] = Time.time - seconds;
            nextTeleportCheck = 0f;
        }
    }
}
