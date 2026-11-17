using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    [DefaultExecutionOrder(100)]
    public sealed class SpeechBubbleDock : MonoBehaviour
    {
        public static readonly List<SpeechBubbleDock> Active = new List<SpeechBubbleDock>();
        public WindupCharacter character;
        public Transform anchor;
        public Text hint;
        public readonly List<TransferableSpeechBubble> Bubbles = new List<TransferableSpeechBubble>();
        public Vector3 ReceivePoint => anchor.position + anchor.up * (lift+StackHeight()+.12f);
        public float ReceiveRadius = .26f;
        // Extra height for this character's stack (staggered when characters stand close).
        public float lift;
        // Panels stay where they are (world-locked) but turn to face where the user's head IS,
        // never where it looks: the viewer point only moves after the user has actually moved
        // ~12 cm, then glides there, so turning the head never makes panels follow.
        // `Facing` remains the shared fallback, set when the guide speaks and at gathering.
        public static Quaternion Facing { get; private set; } = Quaternion.identity;
        public static Vector3 Viewer { get; private set; }
        public static bool HasViewer { get; private set; }
        private static Vector3 viewerTarget;
        private static int viewerFrame = -1;
        public const float ViewerDeadZone = .12f, ViewerGlide = .6f;
        public static void UpdateViewer(Transform head)
        {
            if (head == null || viewerFrame == Time.frameCount) return;
            viewerFrame = Time.frameCount;
            if (!HasViewer) { SnapViewer(head.position); return; }
            if (Vector3.Distance(head.position, viewerTarget) > ViewerDeadZone) viewerTarget = head.position;
            Viewer = Vector3.MoveTowards(Viewer, viewerTarget, Time.deltaTime * ViewerGlide);
        }
        public static void SnapViewer(Vector3 position)
        {
            Viewer = viewerTarget = position;
            HasViewer = true;
        }
        // Upright rotation for a panel at `position` whose front (-Z) faces the viewer's eyes.
        public static Quaternion FacingAt(Vector3 position)
        {
            if (!HasViewer) return Facing;
            Vector3 away = position - Viewer;
            Vector3 flat = Vector3.ProjectOnPlane(away, Vector3.up);
            if (flat.sqrMagnitude < .0001f) return Facing;
            // Lean back toward the eyes, but never lie flat.
            float pitch = Mathf.Clamp(Vector3.SignedAngle(flat, away, Vector3.Cross(Vector3.up, flat)), -35f, 35f);
            Vector3 look = Quaternion.AngleAxis(pitch, Vector3.Cross(Vector3.up, flat.normalized)) * flat.normalized;
            return Quaternion.LookRotation(look, Vector3.up);
        }
        public const float TiltTowardEyes = 16f, ForwardOffset = .06f;
        private static bool facingReady;
        private float highlightUntil;
        public static Quaternion UpdateFacing(Transform head)
        {
            if (!facingReady && head != null) FaceFrom(head.position, head.position + head.forward);
            return Facing;
        }
        // Cards face the viewer standing at `viewer` who is looking toward `focus`.
        public static void FaceFrom(Vector3 viewer, Vector3 focus)
        {
            Vector3 flat = Vector3.ProjectOnPlane(focus - viewer, Vector3.up);
            if (flat.sqrMagnitude < .0001f) return;
            flat.Normalize();
            Vector3 away = Quaternion.AngleAxis(TiltTowardEyes, Vector3.Cross(Vector3.up, flat)) * flat;
            Facing = Quaternion.LookRotation(away, Vector3.up);
            facingReady = true;
            SnapViewer(viewer);
        }
        private void OnEnable(){if(!Active.Contains(this))Active.Add(this);}
        private void OnDisable(){Active.Remove(this);}
        private float StackHeight()
        {
            float y=0;
            foreach(var b in Bubbles)if(b!=null)y+=b.LayoutHeight+.035f;
            return y;
        }
        public void Highlight(){highlightUntil=Time.time+.12f;}
        public void Attach(TransferableSpeechBubble bubble)
        {
            if(bubble.IsHeld)return;
            if(bubble.Owner!=null)bubble.Owner.Remove(bubble);
            Bubbles.Add(bubble);bubble.Owner=this;
            bubble.transform.SetParent(anchor,true);bubble.transform.localScale=Vector3.one;
            Layout();
        }
        public void Remove(TransferableSpeechBubble bubble){Bubbles.Remove(bubble);}
        private void LateUpdate()
        {
            var head=character.meeting.Head;
            // All docked cards share one calm viewing plane. Detached/held cards keep the hand pose.
            if(head!=null){UpdateFacing(head);UpdateViewer(head);anchor.rotation=FacingAt(anchor.position);}
            Layout();
            if(hint!=null)
            {
                hint.gameObject.SetActive(Time.time<highlightUntil);
                hint.text="Release to give to "+character.characterName;
                hint.transform.parent.localPosition=new Vector3(0,lift+StackHeight()+.08f,-.02f);
            }
        }
        public void Layout()
        {
            float y=lift;
            foreach(var bubble in Bubbles)
            {
                if(bubble==null||bubble.IsHeld)continue;
                var box=bubble.Box;
                float height=bubble.LayoutHeight;
                float minY=box==null?-height*.5f:box.center.y-box.size.y*.5f;
                // A few centimetres toward the viewer (-Z) so cards clear monitors/walls behind characters.
                bubble.transform.localPosition=new Vector3(0,y-minY,-ForwardOffset);
                bubble.transform.localRotation=Quaternion.identity;
                y+=height+.035f;
            }
        }
    }
}
