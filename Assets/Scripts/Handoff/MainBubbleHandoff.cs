using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    public sealed class MainBubbleHandoff : MonoBehaviour
    {
        public WindupsMeeting meeting;
        public SpeechBubbleDock[] docks;
        public TransferableSpeechBubble[] bubbles;
        public bool Ready {get;private set;}
        private IEnumerator Start()
        {
            // F78: in airplane mode there is no room (Roomless): no seats, the crew stands on the title sign.
            while(!(meeting.environment.IsReady||meeting.environment.Roomless)||meeting.Head==null)yield return null;
            yield return null; // Let the existing Editor preview set its room-relative eye height.
            var env=meeting.environment;var head=meeting.Head;
            Vector3 forward=Vector3.ProjectOnPlane(head.forward,Vector3.up).normalized;
            if(forward.sqrMagnitude<.5f)forward=Vector3.forward;
            var layout = new ArrivalSeats(env, head.position, forward);
            var used=new List<Vector3>();
            var arrival = GetComponent<WindupsArrival>();
            var aim = new List<Vector3>();
            for (int i = 0; i < docks.Length; i++)
            {
                bool isGuide = arrival != null && docks[i].character == arrival.guide;
                float angle = Mathf.Lerp(-70f, 70f, i / (docks.Length - 1f));
                var ranked = layout.Ranked(angle, isGuide ? env : null);
                if (ranked.Count == 0 && isGuide) ranked = layout.Ranked(angle);
                // F28 intro: the title brings this one in later; the Editor preview still aims at where it would stand.
                if (docks[i].character.movement.HoldForIntro) { if (ranked.Count > 0) aim.Add(ranked[0]); continue; }
                foreach(var p in ranked)
                {
                    bool clear=true;foreach(var q in used)if(Vector3.Distance(p,q)<.32f){clear=false;break;}
                    if(!clear)continue;
                    if(docks[i].character.movement.TryPlaceAt(p)){used.Add(p);break;}
                }
            }
            foreach(var bubble in bubbles)
                foreach(var dock in docks)Physics.IgnoreCollision(bubble.GetComponent<Collider>(),dock.character.GetComponent<Collider>());
            Ready=true;
#if UNITY_EDITOR
            var framed = used.Count > 0 ? used : aim;
            if(!UnityEngine.XR.XRSettings.isDeviceActive&&framed.Count>0)
            {
                Vector3 target=Vector3.zero;foreach(var p in framed)target+=p;target/=framed.Count;target+=Vector3.up*.3f;
                var rig=FindFirstObjectByType<OVRCameraRig>();rig.transform.rotation=Quaternion.LookRotation(target-head.position,Vector3.up);
                if(Camera.main!=null)Camera.main.fieldOfView=72;
                foreach(var dock in docks)dock.character.movement.RoamingEnabled=false;
            }
#endif
        }
        private void Update()
        {
            bool transferring=false;foreach(var b in bubbles)if(b!=null&&b.IsHeld){transferring=true;break;}
            foreach(var dock in docks)
            {
                dock.character.movement.InteractionPaused=transferring;
                if(transferring)dock.character.movement.StopWalking();
            }
        }
    }
}
