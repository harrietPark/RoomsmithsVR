using Oculus.Interaction;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    public sealed class TransferableSpeechBubble : MonoBehaviour
    {
        public string materialKind;
        public string initialMessage;
        public string authorName;
        public Text title,detail,provenance;
        public SpeechBubbleDock startingDock;
        public SpeechBubbleDock Owner {get;internal set;}
        public SpeechBubbleDock Candidate {get;private set;}
        public BoxCollider Box {get;private set;}
        public bool IsHeld => grab!=null&&grab.GrabPoints!=null&&grab.GrabPoints.Count>0;
        // Toy cards are sized to their text, so stack them by their real height.
        public float LayoutHeight => Box==null ? .23f : Mathf.Max(IsToyCard ? .05f : .20f,Box.size.y);
        private bool IsToyCard => (presentation!=null||(presentation=GetComponent<SpeechBubblePresentation>())!=null) && presentation.toyStyle;
        public bool AllowTransfer { get; private set; } = true;
        public event System.Action Released;
        private Grabbable grab;
        private SpeechBubblePresentation presentation;
        private SpeechBubbleDock previousOwner;
        private bool held,releasePending,cancelled;
        private bool textAssigned;
        private void Awake()
        {
            grab=GetComponent<Grabbable>();Box=GetComponent<BoxCollider>();
            presentation=GetComponent<SpeechBubblePresentation>();
            grab.WhenPointerEventRaised+=OnPointer;
        }
        private void Start()
        {
            if (!textAssigned) SetText(authorName, initialMessage);
            if (startingDock != null && Owner == null && !IsHeld) startingDock.Attach(this);
        }
        public void SetText(string author,string message)
        {
            SetText(author, message, message);
        }
        public void SetText(string author,string summaryText,string detailText)
        {
            textAssigned = true;
            authorName=author;
            string message=summaryText ?? string.Empty;
            // Toy cards grow to fit the whole sentence; only the paper look keeps the 48-char cut.
            string summary=IsToyCard||message.Length<=48?message:message.Substring(0,45)+"...";
            if(title!=null&&title.text!=summary)title.text=summary;
            string details=detailText ?? message;
            if(detail!=null&&detail.text!=details)detail.text=details;
            if(provenance!=null&&provenance.text!=author)provenance.text=author;
            if(presentation==null)presentation=GetComponent<SpeechBubblePresentation>();
            if(presentation!=null)presentation.SetAuthor(author);
        }
        public void BeginTopicNote()
        {
            AllowTransfer=false;
            if(presentation==null)presentation=GetComponent<SpeechBubblePresentation>();
            if(presentation!=null)presentation.SetTopicNote(true);
        }
        public void BeginCarriedTopicNote(Transform carrier, Vector3 worldPosition, Quaternion worldRotation)
        {
            AllowTransfer=false;
            if(Owner!=null)Owner.Remove(this);
            Owner=null;
            transform.SetParent(carrier,true);
            transform.SetPositionAndRotation(worldPosition,worldRotation);
            if(presentation==null)presentation=GetComponent<SpeechBubblePresentation>();
            if(presentation!=null)presentation.SetTopicNote(true);
        }
        public void SetInitialOwner(SpeechBubbleDock dock)
        {
            if(Box==null)Box=GetComponent<BoxCollider>();
            dock.Attach(this);
        }
        private void OnPointer(PointerEvent e)
        {
            if(IsHeld&&!held)
            {
                held=true;releasePending=false;cancelled=false;
                previousOwner=Owner;
                if(Owner!=null)Owner.Remove(this);
                Owner=null;transform.SetParent(null,true);
            }
            else if(!IsHeld&&held)
            {
                held=false;releasePending=true;cancelled=e.Type==PointerEventType.Cancel;
            }
        }
        private void LateUpdate()
        {
            if(IsHeld)
            {
                Candidate=AllowTransfer?FindReceiver():null;
                if(Candidate!=null)Candidate.Highlight();
            }
            else if(releasePending)
            {
                releasePending=false;
                var destination=AllowTransfer?(cancelled?previousOwner:FindReceiver()):null;
                if(destination!=null)destination.Attach(this);
                Candidate=null;
                if (!cancelled) Released?.Invoke();
            }
        }
        private SpeechBubbleDock FindReceiver()
        {
            SpeechBubbleDock best=null;float score=float.PositiveInfinity;
            foreach(var dock in SpeechBubbleDock.Active)
            {
                // The next slot above the current stack is the drop target.
                float d=Mathf.Min(Vector3.Distance(transform.position,dock.ReceivePoint),Vector3.Distance(transform.position,dock.anchor.position+dock.anchor.up*.12f));
                if(d<dock.ReceiveRadius&&d<score){best=dock;score=d;}
            }
            return best;
        }
        private void OnDestroy(){if(grab!=null)grab.WhenPointerEventRaised-=OnPointer;if(Owner!=null)Owner.Remove(this);}
    }
}
