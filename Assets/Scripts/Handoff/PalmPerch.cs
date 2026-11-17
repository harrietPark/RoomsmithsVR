using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;
using Pet = RoomCapsule.CapsulePetController;

namespace Windups.Handoff
{
    // F51 (2026-11-12, "if I put a character on my palm I want it to stay on my palm"): a character let go over
    // an open palm that faces up, or caught by one while it falls, stands on that palm and rides the hand. It
    // stays there while the palm is roughly level: tipped past TipAngle for TipTime it slides off and falls as
    // when dropped (and then walks back as always); a hand can pick it off; tracking lost for LostTime lets it
    // fall. One character per palm. The left palm is also where the maker's palette lives: while a character
    // stands on it, or is carried to it, the palette stays away (PalmPerch.Busy).
    // Hand queries go through HandIO, so Editor checks drive it with synthetic hands.
    public sealed class PalmPerch : MonoBehaviour
    {
        public static PalmPerch Instance { get; private set; }
        public WindupsArrival arrival;

        public const float PalmRadius = .065f;      // a falling one is caught within this of the palm's middle
        public const float ReleaseRadius = .11f;    // one let go by a hand: a little wider (it glides to the middle)
        public const float ReleaseAbove = .15f, CatchAbove = .06f, Below = .05f;
        public const float Skin = .016f;            // the palm joints lie inside the hand: the skin is this far out
        public const float StandUp = .64f;          // the palm's normal against straight up to start standing (about 50 degrees)
        // (Tracking often drops while the other hand reaches over the palm: it waits LostTime where it was.)
        public const float TipAngle = 62f, TipTime = .35f, LostTime = 2.5f, OpenFinger = .05f;
        public const float CarryNear = .17f;        // a carried character this near a palm keeps the palette away

        public int Landings { get; private set; }
        public int SlidesOff { get; private set; }
        public int TrackingDrops { get; private set; }

        private sealed class Ride
        {
            public Pet pet;
            public Handedness side;
            public Vector3 feet, lastPalm, velocity, seen;
            public float tippedSince = -1f, lostSince = -1f, since;
            public bool hasLast;
        }

        private readonly List<Ride> rides = new List<Ride>();
        private readonly List<Pet> pets = new List<Pet>();
        private float nextPetScan;

        public static bool Any => Instance != null && Instance.rides.Count > 0;
        public static bool On(Handedness side) => Instance != null && Instance.rides.Exists(r => r.side == side);
        public static bool Riding(Pet pet) => Instance != null && Instance.rides.Exists(r => r.pet == pet);

        // A character stands on this palm, or one in a hand's grab is right over it.
        public static bool Busy(Handedness side)
        {
            if (Instance == null) return false;
            if (On(side)) return true;
            if (!Instance.Palm(side, out Vector3 centre, out _, out _)) return false;
            foreach (var pet in Instance.Pets())
                if (pet != null && pet.Grabbed && Vector3.Distance(pet.transform.position, centre) < CarryNear) return true;
            return false;
        }

        private void Awake()
        {
            Instance = this;
            Pet.PalmUnder = LetGoOverPalm;
        }

        private void OnDestroy()
        {
            if (Instance == this) { Instance = null; Pet.PalmUnder = null; }
        }

        private List<Pet> Pets()
        {
            if (Time.time >= nextPetScan || pets.Count == 0)
            {
                nextPetScan = Time.time + 2f;
                pets.Clear();
                if (arrival != null && arrival.handoff != null && arrival.handoff.docks != null)
                    foreach (var dock in arrival.handoff.docks)
                        if (dock != null && dock.character != null && dock.character.movement != null && dock.character.movement.isActiveAndEnabled)
                            pets.Add(dock.character.movement);
            }
            return pets;
        }

        // The palm of a side: its middle, the normal out of the palm's skin, and whether the hand is open.
        public bool Palm(Handedness side, out Vector3 centre, out Vector3 normal, out bool open)
        {
            centre = normal = Vector3.zero;
            open = false;
            var hand = HandSet.Side(side);
            if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.TryPalm(hand, out centre, out normal)) return false;
            // HandIO.TryPalm's normal points out of the left palm but out of the back of the right hand.
            if (side == Handedness.Right) normal = -normal;
            open = HandIO.Joint(hand, HandJointId.HandMiddleTip, out Pose tip) && Vector3.Distance(tip.position, centre) > OpenFinger;
            return true;
        }

        // A palm this character could stand on now: open, facing up, free, and under its feet.
        private bool PalmFor(Pet pet, bool letGo, out Handedness side)
        {
            side = Handedness.Left;
            float best = float.MaxValue;
            bool found = false;
            Vector3 feet = pet.transform.position;
            foreach (var s in new[] { Handedness.Left, Handedness.Right })
            {
                if (On(s) || !Palm(s, out Vector3 centre, out Vector3 normal, out bool open) || !open) continue;
                if (Vector3.Dot(normal, Vector3.up) < StandUp) continue;
                Vector3 rel = feet - centre;
                float height = Vector3.Dot(rel, normal);
                float flat = (rel - normal * height).magnitude;
                if (flat > (letGo ? ReleaseRadius : PalmRadius) || height < -Below || height > (letGo ? ReleaseAbove : CatchAbove)) continue;
                if (flat < best) { best = flat; side = s; found = true; }
            }
            return found;
        }

        // Asked by the character the moment a hand lets go of it.
        private bool LetGoOverPalm(Pet pet)
        {
            if (!isActiveAndEnabled || pet == null || Riding(pet) || !PalmFor(pet, true, out var side)) return false;
            Begin(pet, side);
            return true;
        }

        private void Begin(Pet pet, Handedness side)
        {
            rides.Add(new Ride { pet = pet, side = side, feet = pet.transform.position, seen = pet.transform.position, since = Time.time });
            Landings++;
            var character = pet.GetComponent<WindupCharacter>();
            var body = WindupBody.Of(character);
            if (body != null)
            {
                body.Squash(.86f, .3f);
                body.Emote(WindupBody.Happy, 1.4f);
            }
            ToySounds.Play(ToySounds.Cue.Press, pet.transform.position, .4f, 1.5f);
            Debug.Log("[Windups Palm] " + pet.name + " stands on the " + side + " palm");
        }

        private void Update()
        {
            // Falling past an open palm: caught.
            foreach (var pet in Pets())
            {
                if (pet == null || pet.IsHeld || pet.State != Pet.MotionState.Falling || Riding(pet)) continue;
                if (!PalmFor(pet, false, out var side)) continue;
                if (pet.StandOnPalm()) Begin(pet, side);
            }
        }

        // After the hands and the grabs of this frame: the riders go where their palms are.
        private void LateUpdate()
        {
            float dt = Mathf.Max(Time.deltaTime, .0001f);
            for (int i = rides.Count - 1; i >= 0; i--)
            {
                var ride = rides[i];
                if (ride.pet == null || !ride.pet.OnPalm) { rides.RemoveAt(i); continue; } // picked off, or gone
                if (!Palm(ride.side, out Vector3 centre, out Vector3 normal, out _))
                {
                    // Hand not seen: it waits where it was, then falls.
                    if (ride.lostSince < 0f) ride.lostSince = Time.time;
                    ride.hasLast = false;
                    if (Time.time - ride.lostSince > LostTime)
                    {
                        TrackingDrops++;
                        End(i, Vector3.zero, "hand lost");
                    }
                    continue;
                }
                ride.lostSince = -1f;
                if (ride.hasLast) ride.velocity = Vector3.Lerp(ride.velocity, (centre - ride.lastPalm) / dt, .4f);
                ride.lastPalm = centre;
                ride.hasLast = true;
                Vector3 target = centre + normal * Skin;
                // Tight on the hand (a short glide in when it lands, then no visible lag).
                float follow = Time.time - ride.since < .18f ? 18f : 60f;
                ride.feet = Vector3.Lerp(ride.feet, target, 1f - Mathf.Exp(-dt * follow));
                Vector3 toward = arrival != null && arrival.handoff != null && arrival.handoff.meeting != null && arrival.handoff.meeting.Head != null
                    ? Vector3.ProjectOnPlane(arrival.handoff.meeting.Head.position - ride.feet, Vector3.up) : Vector3.zero;
                Quaternion turn = toward.sqrMagnitude > 1e-5f ? Quaternion.LookRotation(toward.normalized, Vector3.up) : Quaternion.Euler(0f, ride.pet.transform.eulerAngles.y, 0f);
                ride.pet.RideOnPalm(ride.feet, turn);

                if (Vector3.Angle(normal, Vector3.up) > TipAngle)
                {
                    if (ride.tippedSince < 0f) ride.tippedSince = Time.time;
                    if (Time.time - ride.tippedSince > TipTime)
                    {
                        // Slides off downhill, with a little of the hand's own motion.
                        Vector3 downhill = Vector3.ProjectOnPlane(normal, Vector3.up);
                        Vector3 push = (downhill.sqrMagnitude > 1e-5f ? downhill.normalized * .25f : Vector3.zero) + Vector3.ClampMagnitude(ride.velocity, 1.2f) * .6f;
                        SlidesOff++;
                        End(i, push, "palm tipped");
                    }
                }
                else ride.tippedSince = -1f;
            }
        }

        private void End(int index, Vector3 velocity, string why)
        {
            var ride = rides[index];
            rides.RemoveAt(index);
            if (ride.pet == null) return;
            Debug.Log("[Windups Palm] " + ride.pet.name + " leaves the " + ride.side + " palm (" + why + ")");
            ride.pet.LeavePalm(velocity);
        }
    }
}
