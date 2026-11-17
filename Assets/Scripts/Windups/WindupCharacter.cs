using System.Collections.Generic;
using RoomCapsule;
using UnityEngine;
using UnityEngine.UI;

namespace Windups
{
    public sealed class WindupCharacter : MonoBehaviour
    {
        public string characterName;
        public CapsulePetController movement;
        public Transform visual;
        public Transform bubble;
        public Text bubbleText;
        public Text nameText;
        public Button bubbleButton;
        public WindupsMeeting meeting;
        public Handoff.TransferableSpeechBubble authoredBubble;
        public readonly Queue<MeetingLine> Pending = new Queue<MeetingLine>();
        public string Cue { get; private set; } = "idle";
        // F25 studio: what this character watches (overrides the meeting speaker / the user) and
        // a small extra hop, both set every frame by the studio session.
        [System.NonSerialized] public Transform lookTarget;
        [System.NonSerialized] public float hop;
        private Vector3 originalScale;
        private Vector3 originalPosition;
        private MeetingLine presented;
        // The line its card shows: the one being said, else the next one waiting.
        public MeetingLine CurrentLine => presented ?? (Pending.Count > 0 ? Pending.Peek() : null);
        public bool IsPresenting => presented != null;

        private void Awake()
        {
            originalScale = visual.localScale;
            originalPosition = visual.localPosition;
            bubbleButton.onClick.AddListener(SelectOpinion);
        }

        public void SelectOpinion() => meeting.Select(this, true);
        public void SetCue(string value)
        {
            bool changed = Cue != value;
            Cue = value;
            // In the arrival meeting the card itself shows "thinking" (a small pill of dots).
            if (changed && authoredBubble != null && meeting != null && meeting.UsesArrivalSeats) Refresh();
        }
        private readonly HashSet<string> received = new HashSet<string>();
        public void Enqueue(MeetingLine line)
        {
            if (!received.Add(line.id)) return;
            // Unread opinions remain available while new reactions arrive.
            Pending.Enqueue(line); Refresh();
        }
        public void Present(MeetingLine line) { presented = line; Cue = line.cue; Refresh(); }
        public void Finish() { presented = null; Cue = "idle"; Refresh(); }

        public void Refresh()
        {
            nameText.text = characterName;
            if(authoredBubble!=null)
            {
                if (meeting.UsesArrivalSeats)
                {
                    var line = presented ?? (Pending.Count > 0 ? Pending.Peek() : null);
                    authoredBubble.gameObject.SetActive(line != null || authoredBubble.IsHeld || Cue == "thinking");
                    if (line != null)
                    {
                        string summary = line.summary;
                        // "+N" waiting lines on the docked card only, not on the paper in the hand.
                        if (presented == null && Pending.Count > 1 && !authoredBubble.IsHeld) summary += "  +" + (Pending.Count - 1);
                        authoredBubble.SetText(characterName, summary, line.body);
                        // Presented: open to the full sentence. Waiting: back to the short summary.
                        var card = authoredBubble.GetComponent<Windups.BubbleLab.TangibleBubble>();
                        // A card in the user's hand is opened/folded by the hand, not the meeting.
                        if (!authoredBubble.IsHeld)
                        {
                            if (presented != null) card?.OpenFully();
                            else card?.CloseToCompact();
                        }
                    }
                    // No separate "Read"/"Choose" button: tapping the card itself presents or
                    // chooses the line (Handoff.BubbleInteractions).
                    bubble.gameObject.SetActive(false);
                }
                else
                {
                    bubble.gameObject.SetActive(false);
                    authoredBubble.SetText(characterName, presented != null ? presented.body : Pending.Count > 0 ? Pending.Peek().summary : authoredBubble.initialMessage);
                }
                return;
            }
            bubble.gameObject.SetActive(presented != null || Pending.Count > 0);
            if (presented != null) bubbleText.text = presented.body;
            else if (Pending.Count > 0)
                bubbleText.text = Pending.Peek().summary + (Pending.Count > 1 ? "\n+" + (Pending.Count - 1) + " waiting" : "");
        }

        private void LateUpdate()
        {
            var head = meeting.Head;
            if (head != null)
            {
                // Canvas front faces along local -Z.
                bubble.rotation = Quaternion.LookRotation(bubble.position - head.position, Vector3.up);
                nameText.transform.parent.rotation = bubble.rotation;
            }
            float phase = Time.time * 3f + transform.GetSiblingIndex();
            float bounce = 0;
            float tilt = 0;
            if (!movement.IsHeld)
            {
                if (Cue == "thinking") tilt = Mathf.Sin(phase) * 9f;
                if (Cue == "agree" || Cue == "encourage") bounce = Mathf.Abs(Mathf.Sin(phase * 1.5f)) * 0.009f;
                if (Cue == "challenge") tilt = Mathf.Sin(phase * 2) * 12f;
                if (Cue == "speak" || Cue == "propose") bounce = Mathf.Abs(Mathf.Sin(phase)) * 0.004f;
            }
            visual.localPosition = originalPosition + Vector3.up * (bounce + (movement.IsHeld ? 0f : hop));
            visual.localScale = originalScale * (1f + Mathf.Sin(phase) * 0.012f);
            Transform target = lookTarget != null ? lookTarget
                : meeting.Speaker != null && !meeting.IsListening && !meeting.UserHolding ? meeting.Speaker.transform : head;
            Vector3 direction = target == null ? transform.forward : target.position - transform.position;
            direction.y = 0;
            var rotation = direction.sqrMagnitude > 0.001f ? Quaternion.LookRotation(direction) : transform.rotation;
            visual.rotation = Quaternion.Slerp(visual.rotation, rotation * Quaternion.Euler(0, 0, tilt), Time.deltaTime * 6);
        }
    }
}
