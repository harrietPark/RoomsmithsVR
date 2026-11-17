using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F45 (2026-11-12): at the first note Maker asks "What do you want to make? Tell me with the mic, or we can
    // look around your room for ideas!" with a "Find ideas" pill just above its speech card (it stays over Maker's
    // head when the card goes). Pressing it (a fingertip on it, a pinch near it or with the hand's ray, an Editor
    // click) starts the idea meeting (MakerSession.Spots.cs). Until then the crew never looks around on its own;
    // asking out loud ("look around", "find ideas", "any ideas?") counts as pressing it, and "I don't know" only
    // points at it. Saying what to make is just saying it: the pill goes.
    // This pill is for the arrival, before the session begins. Once it has begun, questions put their answers in
    // reply pills of the same look (MakerSession.Questions.cs, F44: IdeaReplies at "what shall we make today?").
    public sealed partial class MakerSession
    {
        public const string IdeasButtonLabel = "Find ideas";
        public bool IdeasButtonShown => ideasButton != null && ideasButton.gameObject.activeSelf;
        public Transform IdeasButtonView => ideasButton;
        public int IdeasPresses { get; private set; }
        public int IdeasPulses { get; private set; }
        public string IdeasTrigger { get; private set; } = "";

        private const float IdeasW = .066f, IdeasGap = .008f;
        private Transform ideasButton, ideasPill;
        private Vector3 ideasOffset;          // from Maker's feet to the pill (kept when its card goes)
        private bool ideasOffsetKnown, ideasPulsedQuiet, ideasPressing;
        private float ideasPulseAt = -10f, ideasShownAt;
        private readonly HashSet<Hand> ideasTouch = new HashSet<Hand>();
        private readonly FingerPress ideasPress = new FingerPress(); // F60
        private readonly Dictionary<Hand, bool> ideasWasPinching = new Dictionary<Hand, bool>();

        // ------------------------------------------------------------ show, hide, press

        public void ShowIdeasButton()
        {
            if (arrival == null) return;
            EnsureCrew();
            BuildIdeasButton();
            ideasPulsedQuiet = false;
            ideasPressing = false;
            if (IdeasButtonShown) return;
            ideasOffsetKnown = false;
            ideasShownAt = Time.time;
            ideasButton.gameObject.SetActive(true);
            ideasButton.position = IdeasTarget();
            ideasButton.rotation = SpeechBubbleDock.FacingAt(ideasButton.position);
            ideasPill.localScale = Vector3.zero;
            ToySounds.Play(ToySounds.Cue.Ask, ideasButton.position, .6f, 1f); // F76
        }

        public void HideIdeasButton()
        {
            if (ideasButton != null) ideasButton.gameObject.SetActive(false);
            ideasTouch.Clear();
            ideasPressing = false;
        }

        // A gentle nudge toward the pill (a quiet while, or "I don't know"): it bounces, nothing more.
        public void PulseIdeasButton()
        {
            if (!IdeasButtonShown) return;
            IdeasPulses++;
            ideasPulseAt = Time.time;
            ToySounds.Play(ToySounds.Cue.Pop, ideasButton.position, .25f, 1.7f);
        }

        // Tests and the Editor: press it as a fingertip would (it pops, then the meeting starts).
        public void PressIdeasButton()
        {
            if (!IdeasButtonShown || ideasPressing) return;
            ideasPressing = true;
            PressRaised(ideasPill); // F77
            ToySounds.Play(ToySounds.Cue.Press, ideasButton.position, .45f, 1.7f);
            ToyFx.Puff(ideasButton.position, .004f, 2);
            StartCoroutine(PressedIdeas());
        }

        private IEnumerator PressedIdeas()
        {
            for (float k = 0f; k < 1f && ideasPill != null; k += Time.deltaTime / .2f)
            {
                ideasPill.localScale = Vector3.one * (1f + Mathf.Sin(k * Mathf.PI) * .2f);
                yield return null;
            }
            FindIdeas("button");
        }

        // The idea meeting, because the person asked for it (the pill, or out loud).
        public void FindIdeas(string trigger)
        {
            IdeasTrigger = trigger;
            if (trigger == "button") IdeasPresses++;
            HideIdeasButton();
            Heard();
            // At the first note: the meeting begins with nothing chosen (the blank note waits, WindupsArrival.TopicPending).
            if (arrival != null && arrival.CurrentPhase == WindupsArrival.Phase.WaitingForTopic) { arrival.BeginQuietMeeting(trigger); return; }
            if (!Begun || InMeeting) return;
            Talk.Add(trigger == "button" ? "The person pressed Find ideas." : "The person asked the crew to look around for ideas.");
            Current = Step.Suggesting;
            StartCoroutine(Suggest(trigger));
        }

        // "I don't know what to make": no looking around unasked; Maker points at the ways to get ideas.
        private void PointAtIdeasButton()
        {
            AskWith(Crew.Maker, "No worries! Tap below and we'll look around for ideas.", false, IdeaReplies);
        }

        // ------------------------------------------------------------ the pill

        // Like the reply pills (MakerSession.Questions.cs): a light face with Maker's colour around it.
        private void BuildIdeasButton()
        {
            if (ideasButton != null) return;
            ideasButton = new GameObject("Find Ideas").transform;
            ideasButton.SetParent(transform, false);
            Color role = MakerRoles.Color(Crew.Maker), face = Color.Lerp(role, Color.white, .8f);
            ideasPill = Panel("Find Ideas Pill", IdeasW, ChipHeight, face, out var text, ChipFont);
            text.text = IdeasButtonLabel;
            text.fontStyle = FontStyles.Bold;
            MakeRaised(ideasPill, IdeasW, ChipHeight, face, role); // F77: a raised button like the reply pills
            ideasPill.SetParent(ideasButton, false);
            ideasPill.localPosition = new Vector3(0f, ChipHeight * .5f, -.002f);
            ideasButton.gameObject.SetActive(false);
        }

        // Just above Maker's card while it shows; when it goes, where it was over Maker's feet.
        private Vector3 IdeasTarget()
        {
            if (!cast.TryGetValue(Crew.Maker, out var owner) || owner == null) return FrontPoint(.45f, -.2f, out _);
            Vector3 feet = owner.transform.position;
            var bubble = owner.authoredBubble;
            if (bubble != null && bubble.gameObject.activeInHierarchy)
            {
                var b = new Bounds(bubble.transform.position, Vector3.zero);
                foreach (var r in bubble.GetComponentsInChildren<Renderer>())
                    if (r.enabled && !(r is ParticleSystemRenderer)) b.Encapsulate(r.bounds);
                if (b.size.y > .01f && b.size.y < .5f)
                {
                    ideasOffset = new Vector3(bubble.transform.position.x, b.max.y + IdeasGap, bubble.transform.position.z) - feet;
                    ideasOffsetKnown = true;
                }
            }
            if (!ideasOffsetKnown) { ideasOffset = Vector3.up * .26f; ideasOffsetKnown = true; }
            Vector3 at = feet + ideasOffset;
            return at + AheadOfBubbles(at, IdeasW * .5f + .004f, ChipHeight * .5f + .004f); // F81: never behind a speech card
        }

        private void IdeasButtonFrame()
        {
            if (!IdeasButtonShown) return;
            // Shown only while Maker asks what to make at the first note (the arrival hides it otherwise too).
            if (Begun || arrival == null || arrival.CurrentPhase != WindupsArrival.Phase.WaitingForTopic) { HideIdeasButton(); return; }
            float blend = 1f - Mathf.Exp(-Time.deltaTime * 10f);
            ideasButton.position = Vector3.Lerp(ideasButton.position, IdeasTarget(), blend);
            ideasButton.rotation = SpeechBubbleDock.FacingAt(ideasButton.position);
            if (!ideasPressing)
            {
                float grow = Mathf.Clamp01((Time.time - ideasShownAt) / .22f);
                float since = Time.time - ideasPulseAt;
                float bounce = since < .8f ? 1f + .16f * Mathf.Sin(since / .8f * Mathf.PI * 2f) * (1f - since / .8f) : 1f;
                ideasPill.localScale = Vector3.one * Mathf.SmoothStep(0f, 1f, grow) * (1f + Mathf.Sin(grow * Mathf.PI) * .18f * (1f - grow)) * bounce;
            }
            if (ideasPressing || ideasPill.localScale.x < .5f) return;
            // A fingertip pushing it, or a pinch on it (near, or aimed with the hand's ray).
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) { if (hand != null) { ideasPress.Forget(hand); ideasWasPinching.Remove(hand); } continue; }
                bool on = HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip) && OnIdeasPill(tip.position, PressMargin, PressNear, PressThrough); // F67
                if (ideasPress.Step(this, hand, on)) { PressLog("Find ideas", hand); PressIdeasButton(); return; }
                bool pinching = HandIO.Pinching(hand);
                ideasWasPinching.TryGetValue(hand, out bool before);
                ideasWasPinching[hand] = pinching;
                if (!pinching || before) continue;
                if (HandRules && HeldBy(hand) != null) continue; // F66: a hand that holds something presses nothing
                bool aimed = HandIO.Pointer(hand, out Pose pointer);
                if (IdeasButtonPinch(PinchPoint(hand), aimed ? new Ray(pointer.position, pointer.forward) : (Ray?)null)) return;
            }
#if UNITY_EDITOR
            if (!UnityEngine.XR.XRSettings.isDeviceActive && Camera.main != null &&
                UnityEngine.InputSystem.Mouse.current is { } mouse && mouse.leftButton.wasPressedThisFrame)
                IdeasButtonPinch(Vector3.positiveInfinity, Camera.main.ScreenPointToRay(mouse.position.ReadValue()));
#endif
        }

        // A pinch near the pill, or the hand's ray (or the Editor mouse) on it.
        private bool IdeasButtonPinch(Vector3 point, Ray? ray)
        {
            if (!IdeasButtonShown || ideasPressing) return false;
            bool hit = OnIdeasPill(point, .012f, -.03f, .03f);
            if (!hit && ray.HasValue && new Plane(ideasButton.forward, ideasPill.position).Raycast(ray.Value, out float d) && d > 0f && d < 4f)
                hit = OnIdeasPill(ray.Value.GetPoint(d), .004f + d * .012f, -.01f, .01f); // F82: a little slack per metre (the far press was tight)
            if (!hit) return false;
            PressIdeasButton();
            return true;
        }

        private bool OnIdeasPill(Vector3 world, float margin, float back, float front)
        {
            if (float.IsInfinity(world.x)) return false;
            Vector3 l = ideasButton.InverseTransformPoint(world) - ideasPill.localPosition;
            return Mathf.Abs(l.x) <= IdeasW * .5f + margin && Mathf.Abs(l.y) <= ChipHeight * .5f + margin && l.z >= back && l.z <= front;
        }

        // Quiet for a while at the first note with the pill up: it bounces once (no line, no looking around).
        public void IdeasQuiet()
        {
            if (!IdeasButtonShown || ideasPulsedQuiet) return;
            ideasPulsedQuiet = true;
            PulseIdeasButton();
        }
    }
}
