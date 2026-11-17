using Oculus.Interaction;
using RoomCapsule;
using UnityEngine;

namespace Windups.Handoff
{
    public sealed class HandoffEditorPreview : MonoBehaviour
    {
        public MainBubbleHandoff system;
#if UNITY_EDITOR
        private Grabbable selected;
        private Pose pose;
        private Plane plane;
        private Vector3 offset;
        private int bubbleIndex,recipientIndex=1;
        // Mouse press waiting to become a drag (moved) or a tap (released in place).
        private Grabbable pressed;
        private PaperSnapshot pressedCopy;
        // A copy pulled out of a deck: its Grabbable sets up in Start, so the grab waits a frame.
        private TransferableSpeechBubble promoted;
        private int promotedFrame;
        private Vector2 pressedAt;
        private Rect area=new Rect(12,12,330,250);
        private WindupsArrival arrival;
        const int Pointer=981001;
        public void Take(Grabbable target)
        {
            Release(true);selected=target;pose=new Pose(target.transform.position,target.transform.rotation);
            target.ProcessPointerEvent(new PointerEvent(Pointer,PointerEventType.Hover,pose));
            target.ProcessPointerEvent(new PointerEvent(Pointer,PointerEventType.Select,pose));
        }
        public void MoveTo(Vector3 position)
        {
            if(selected==null)return;pose.position=position;
            selected.ProcessPointerEvent(new PointerEvent(Pointer,PointerEventType.Move,pose));
        }
        public void Release(bool cancel=false)
        {
            if(selected==null)return;
            selected.ProcessPointerEvent(new PointerEvent(Pointer,cancel?PointerEventType.Cancel:PointerEventType.Unselect,pose));
            selected.ProcessPointerEvent(new PointerEvent(Pointer,PointerEventType.Unhover,pose));selected=null;
        }
        private void OnGUI()
        {
            if(UnityEngine.XR.XRSettings.isDeviceActive||system==null)return;
            if(arrival==null)arrival=FindFirstObjectByType<WindupsArrival>();
            bool meeting=arrival!=null&&arrival.CurrentPhase==WindupsArrival.Phase.Meeting;
            area.height=meeting?400:arrival!=null&&arrival.CurrentPhase<WindupsArrival.Phase.Meeting?300:250;
            GUILayout.BeginArea(area,GUI.skin.box);
            if(meeting)MeetingHelp();
            else
            {
                GUILayout.Label("SIX CAPSULES / BUBBLE HANDOFF");
                GUILayout.Label("Drag a capsule or a bubble in the Game view.");
                GUILayout.Label("Drop near another head: stack above its bubble.");
                if(!system.Ready)GUILayout.Label("Waiting for MRUK room navigation...");
                if(system.Ready)
                {
                    bool roaming=system.docks[0].character.movement.RoamingEnabled;
                    bool value=GUILayout.Toggle(roaming,"Let capsules roam (Editor preview)");
                    if(value!=roaming)foreach(var dock in system.docks){dock.character.movement.RoamingEnabled=value;if(!value)dock.character.movement.StopWalking();}
                }
                if(arrival!=null&&system.Ready&&arrival.CurrentPhase<WindupsArrival.Phase.Meeting)
                {
                    GUILayout.Label("Card interactions start in the meeting:");
                    GUILayout.BeginHorizontal();
                    if(GUILayout.Button("Skip to meeting (scripted)"))arrival.PreviewSkipToMeeting("I want to make an app for kids.",true);
                    if(GUILayout.Button("(AI)"))arrival.PreviewSkipToMeeting("I want to make an app for kids.",false);
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                if(GUILayout.Button("Bubble <"))bubbleIndex=(bubbleIndex+5)%6;
                if(GUILayout.Button("Bubble >"))bubbleIndex=(bubbleIndex+1)%6;
                GUILayout.EndHorizontal();
                var bubble=system.bubbles[bubbleIndex];GUILayout.Label(bubble.authorName+" / "+bubble.materialKind);
                if(GUILayout.Button("Recipient: "+system.docks[recipientIndex].character.characterName))recipientIndex=(recipientIndex+1)%system.docks.Length;
                if(GUILayout.Button("Give selected bubble to recipient"))
                {
                    Take(bubble.GetComponent<Grabbable>());MoveTo(system.docks[recipientIndex].ReceivePoint);Release();
                }
            }
            GUILayout.EndArea();
            Event e=Event.current;
            if(e.button!=0||Camera.main==null)return;
            Vector2 screen=new Vector2(e.mousePosition.x,Screen.height-e.mousePosition.y);
            Ray ray=Camera.main.ScreenPointToRay(screen);
            if(e.type==EventType.MouseDown&&!area.Contains(e.mousePosition))
            {
                // Stickers, the basket and the notebook take clicks themselves.
                if(BubbleInteractions.Instance!=null&&BubbleInteractions.Instance.OwnsRay(ray))return;
                // Maker: the session takes clicks on the desk (measuring, coasters) itself.
                if(MakerSession.Instance!=null&&MakerSession.Instance.Begun)return;
                // An earlier line in a deck: click = open the deck, drag = pull it out as a card.
                var copy=BubbleInteractions.Instance!=null?BubbleInteractions.Instance.DeckSnapshotHit(ray,out float copyDistance):null;
                if(copy!=null)
                {
                    plane=new Plane(Camera.main.transform.forward,copy.transform.position);
                    plane.Raycast(ray,out float copyHit);offset=copy.transform.position-ray.GetPoint(copyHit);
                    pressedCopy=copy;pressedAt=e.mousePosition;e.Use();return;
                }
                Grabbable target=null;float best=float.PositiveInfinity;
                foreach(var hit in Physics.RaycastAll(ray,10))
                {
                    var bubbleHit=hit.collider.GetComponentInParent<TransferableSpeechBubble>();
                    var petHit=hit.collider.GetComponentInParent<CapsulePetController>();
                    var g=bubbleHit!=null?bubbleHit.GetComponent<Grabbable>():petHit!=null?petHit.GetComponent<Grabbable>():null;
                    if(g!=null&&hit.distance<best){best=hit.distance;target=g;}
                }
                if(target!=null)
                {
                    plane=new Plane(Camera.main.transform.forward,target.transform.position);
                    plane.Raycast(ray,out float hit);offset=target.transform.position-ray.GetPoint(hit);
                    // Grab only once the mouse moves; a click in place is a tap.
                    pressed=target;pressedAt=e.mousePosition;e.Use();
                }
            }
            if(e.type==EventType.MouseDrag&&pressed!=null&&selected==null&&(e.mousePosition-pressedAt).magnitude>6f)
            {
                Take(pressed);pressed=null;
            }
            if(e.type==EventType.MouseDrag&&pressedCopy!=null&&(e.mousePosition-pressedAt).magnitude>6f)
            {
                promoted=BubbleInteractions.Instance.PromoteSnapshot(pressedCopy);pressedCopy=null;promotedFrame=Time.frameCount;
            }
            if(e.type==EventType.MouseDrag&&promoted!=null&&selected==null&&Time.frameCount>promotedFrame+1)
            {
                Take(promoted.GetComponent<Grabbable>());promoted=null;
            }
            if(e.type==EventType.MouseDrag&&selected!=null)
            {
                if(plane.Raycast(ray,out float hit))MoveTo(ray.GetPoint(hit)+offset);e.Use();
            }
            if(e.type==EventType.MouseUp&&pressed!=null)
            {
                var card=pressed.GetComponent<TransferableSpeechBubble>();
                pressed=null;
                if(card!=null)BubbleInteractions.Instance?.EditorTap(card);
                e.Use();
            }
            if(e.type==EventType.MouseUp&&pressedCopy!=null){BubbleInteractions.Instance?.TapSnapshot(pressedCopy);pressedCopy=null;e.Use();}
            if(e.type==EventType.MouseUp&&promoted!=null){BubbleInteractions.Instance?.SettleFree(promoted);promoted=null;e.Use();}
            if(e.type==EventType.MouseUp&&selected!=null){Release();e.Use();}
        }

        private void MeetingHelp()
        {
            GUILayout.Label("MEETING CARDS (Editor stand-ins for hands)");
            GUILayout.Label("Hover a card: emoji stickers. Click one\n  (or press 1-4: like / love / hmm / nah).");
            GUILayout.Label("Click a card: hear more (a placed card: expand).");
            GUILayout.Label("Drag a card: hold it (everyone goes quiet).");
            GUILayout.Label("  ...drop on the ring = small key-idea chip, on a\n  head = hand it over, anywhere else = it stays\n  there (on another placed card = a pile).");
            GUILayout.Label("While hovering a card: F read up close, E expand,\n  R keep in ring, H hand to next, X crumple.\n  Left/Right arrows: flip a pile or a deck\n  (a thumb swipe on Quest).");
            GUILayout.Label("Click the deck above a card: earlier lines.");
            GUILayout.Label("Click Benefit's basket: shake out discards.");
            GUILayout.Label("Click Decisions' notebook: meeting notes.");
            var interactions=BubbleInteractions.Instance;
            if(interactions!=null)
                GUILayout.Label("Key "+interactions.KeyIdeas.Count+" · Basket "+interactions.Discarded.Count+" · Stickers "+interactions.ReactionLog.Count+
                    (system.meeting.Busy?" · thinking…":""));
        }
        private void OnDisable(){Release(true);}
#endif
    }
}
