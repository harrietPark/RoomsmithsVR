using Oculus.Interaction;
using UnityEngine;

namespace Windups.BubbleLab
{
    // The harness simulates hands through ISDK, never by assigning shape states.
    public sealed class BubbleLabPreview : MonoBehaviour
    {
        public TangibleBubble bubble;
        private bool placed;
#if UNITY_EDITOR
        private int simulated;
        private float gap=.28f;
        private Vector3 center;
        private Quaternion rotation;
        private Transform[] grips;
        private Material gripMaterial;
        private int dragging=-1;
        private bool demo;
        private float demoTime;
        public void SetHands(int count)
        {
            var g=bubble.GetComponent<Grabbable>();
            for(int i=simulated-1;i>=0;i--) {var p=PoseFor(i);g.ProcessPointerEvent(new PointerEvent(970000+i,PointerEventType.Unselect,p));g.ProcessPointerEvent(new PointerEvent(970000+i,PointerEventType.Unhover,p));}
            simulated=0;center=bubble.transform.position;rotation=bubble.transform.rotation;
            gap=bubble.IsCrumpled ? .07f : bubble.Width;
            for(int i=0;i<count;i++) {var p=PoseFor(i);g.ProcessPointerEvent(new PointerEvent(970000+i,PointerEventType.Hover,p));g.ProcessPointerEvent(new PointerEvent(970000+i,PointerEventType.Select,p));}
            simulated=count;
        }
        private Pose PoseFor(int index) => new Pose(center+rotation*Vector3.right*(index==0?-.5f:.5f)*gap,rotation);
        public void MoveGap(float distance)
        {
            gap=distance;var g=bubble.GetComponent<Grabbable>();
            for(int i=0;i<simulated;i++)g.ProcessPointerEvent(new PointerEvent(970000+i,PointerEventType.Move,PoseFor(i)));
        }
        public void PlayGesture()
        {
            SetHands(2);MoveGap(.28f);demoTime=0;demo=true;
        }
        private void PreviewInput()
        {
            if(grips==null)
            {
                gripMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit"));gripMaterial.color=new Color(.18f,.62f,.84f);
                grips=new Transform[2];
                for(int i=0;i<2;i++) {var go=GameObject.CreatePrimitive(PrimitiveType.Sphere);Destroy(go.GetComponent<Collider>());go.name="EditorPinchMarker";go.GetComponent<Renderer>().sharedMaterial=gripMaterial;go.transform.localScale=Vector3.one*.016f;grips[i]=go.transform;}
                // Editor-only studio view. Device passthrough is untouched.
                Camera.main.clearFlags=CameraClearFlags.SolidColor;Camera.main.backgroundColor=new Color(.17f,.19f,.22f);Camera.main.fieldOfView=60;
            }
            if(demo)
            {
                demoTime+=Time.deltaTime;float t=demoTime;
                if(t<2)MoveGap(Mathf.Lerp(.28f,.52f,Mathf.SmoothStep(0,1,t/2)));
                else if(t<3)MoveGap(.52f);
                else if(t<6)MoveGap(Mathf.Lerp(.52f,.05f,Mathf.SmoothStep(0,1,(t-3)/3)));
                else if(t<7)MoveGap(.05f);
                else if(t<10)MoveGap(Mathf.Lerp(.05f,.28f,Mathf.SmoothStep(0,1,(t-7)/3)));
                else {demo=false;SetHands(0);}
            }
            for(int i=0;i<2;i++)
            {
                if(simulated>0)grips[i].position=PoseFor(i).position-rotation*Vector3.forward*.012f;
                else grips[i].position=bubble.transform.position+bubble.transform.right*(i==0?-.5f:.5f)*(bubble.IsCrumpled ? .07f : bubble.Width)-bubble.transform.forward*.012f;
            }
        }
        private void OnGUI()
        {
            if(UnityEngine.XR.XRSettings.isDeviceActive)return;
            GUILayout.BeginArea(new Rect(18,18,370,280),GUI.skin.box);
            GUILayout.Label("PAPER BUBBLE / Editor gesture preview");
            GUILayout.Label("Drag either BLUE DOT inward / outward.");
            GUILayout.Label("Both grips move symmetrically in this preview.");
            GUILayout.Space(8);GUILayout.Label(bubble.State+"  |  fold "+Mathf.RoundToInt(bubble.Crumple*100)+"%");
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("One hand")){demo=false;SetHands(1);}
            if(GUILayout.Button("Two hands")){demo=false;SetHands(2);}
            if(GUILayout.Button("Release")){demo=false;SetHands(0);}
            GUILayout.EndHorizontal();
            if(GUILayout.Button(demo?"Stop gesture preview":"Play pull / open / crumple / restore"))
            {if(demo)demo=false;else PlayGesture();}
            if(simulated==2&&!demo)
            {
                GUILayout.Label("Grip distance: "+gap.ToString("F2")+" m");
                float next=GUILayout.HorizontalSlider(gap,.045f,.56f);
                if(Mathf.Abs(next-gap)>.0001f)MoveGap(next);
            }
            GUILayout.Label("Pull: edge tension -> wider reading surface\nPush: wrinkles -> folded bundle\nPull again: restore the same paper");
            GUILayout.EndArea();
            HandlePointer(Event.current);
        }
        private void HandlePointer(Event e)
        {
            if(demo||grips==null||Camera.main==null||e.button!=0)return;
            Vector2 point=new Vector2(e.mousePosition.x,Screen.height-e.mousePosition.y);
            if(e.type==EventType.MouseDown&&!new Rect(18,18,370,280).Contains(e.mousePosition))
                for(int i=0;i<2;i++)
                {
                    Vector3 screen=Camera.main.WorldToScreenPoint(grips[i].position);
                    if(Vector2.Distance(point,screen)<32){if(simulated!=2)SetHands(2);dragging=i;e.Use();break;}
                }
            if(e.type==EventType.MouseDrag&&dragging>=0)
            {
                var plane=new Plane(rotation*Vector3.forward,center);var ray=Camera.main.ScreenPointToRay(point);
                if(plane.Raycast(ray,out float hit))
                {
                    float x=(Quaternion.Inverse(rotation)*(ray.GetPoint(hit)-center)).x;
                    MoveGap(Mathf.Clamp(Mathf.Abs(x)*2,.045f,.56f));
                }
                e.Use();
            }
            if(e.type==EventType.MouseUp&&dragging>=0){dragging=-1;e.Use();}
        }
        private void OnDestroy()
        {
            if(grips!=null)foreach(var grip in grips)if(grip!=null)Destroy(grip.gameObject);
            if(gripMaterial!=null)Destroy(gripMaterial);
        }
#endif
        private void Update()
        {
            if(!placed&&Time.timeSinceLevelLoad>.5f)
            {
                var rig=FindFirstObjectByType<OVRCameraRig>();if(rig==null)return;
#if UNITY_EDITOR
                if(!UnityEngine.XR.XRSettings.isDeviceActive)rig.transform.position=Vector3.up*1.6f;
#endif
                var head=rig.centerEyeAnchor;
                bubble.transform.SetPositionAndRotation(head.position+head.forward*.55f-head.up*.06f,Quaternion.LookRotation(head.forward,Vector3.up));placed=true;
            }
#if UNITY_EDITOR
            if(placed&&!UnityEngine.XR.XRSettings.isDeviceActive)PreviewInput();
#endif
        }
    }
}

