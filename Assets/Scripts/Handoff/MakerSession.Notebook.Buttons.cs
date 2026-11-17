using System;
using System.Collections.Generic;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    public sealed partial class MakerSession
    {
        private readonly List<(Rect rect,NotebookButton3D button)> notebookCaps=new List<(Rect,NotebookButton3D)>();
        private readonly HashSet<Hand> notebookTouchDown=new HashSet<Hand>();
        private Rect NotebookRect(int page,float x,float y,float w,float h)
        {
            float centre=(page==0?-1:1)*(PageW*.5f+.003f);
            return new Rect(centre-PageW*.5f+x*.001f,PageH*.5f-(y+h)*.001f,w*.001f,h*.001f);
        }
        private void BookButton(int page,string title,float x,float y,float width,Action click,bool enabled=true)
        {
            const float height=16;
            var rect=NotebookRect(page,x,y,width,height);
            var root=new GameObject("Notebook Button "+title).transform;root.SetParent(notebookView,false);
            root.localPosition=new Vector3(rect.center.x,rect.center.y,-.006f);
            var button=root.gameObject.AddComponent<NotebookButton3D>();button.interactable=enabled;button.clicked=click;
            Color top=enabled?(title=="x"?new Color(.9f,.70f,.60f):new Color(.73f,.85f,.96f)):new Color(.82f,.83f,.82f);
            Color side=enabled?new Color(.34f,.52f,.70f):new Color(.65f,.66f,.65f);
            button.footMesh=ToyMeshes.RoundedSlab(width*.001f+.001f,.017f,.005f,.003f,side,side*.85f,side,"Button socket");
            var foot=Part("Button socket",button.footMesh);foot.SetParent(root,false);foot.localPosition=Vector3.forward*.003f;
            button.capMesh=ToyMeshes.RoundedSlab(width*.001f-.001f,.015f,.0045f,.003f,top,side,side,"Button cap");
            button.cap=Part("Raised cap",button.capMesh);button.cap.SetParent(root,false);
            var canvas=new GameObject("Button label",typeof(RectTransform),typeof(Canvas));
            var r=(RectTransform)canvas.transform;r.SetParent(button.cap,false);r.sizeDelta=new Vector2(width,16);
            r.localScale=Vector3.one*.001f;r.localPosition=new Vector3(0,0,-.0003f);
            canvas.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;
            var text=HatIdeaCard.Label(r,font,1,0,width-2,16,7,8,enabled?notebookInk:new Color(.48f,.49f,.5f),FontStyles.Normal,TextAlignmentOptions.Center);
            text.text=title;text.raycastTarget=false;
            notebookWidgets.Add(root.gameObject);notebookCaps.Add((rect,button));
            if(enabled)notebookButtons.Add((rect,button.Press));
        }

        private readonly FingerPress notebookPress = new FingerPress(); // F60
        private void NotebookButtonFrame()
        {
            if(NotebookHeld)return;
            foreach(var hand in HandSet.Both())
            {
                if(hand==null||!hand.isActiveAndEnabled||!HandIO.Connected(hand)) {notebookPress.Forget(hand);continue;}
                if(!HandIO.Joint(hand, HandJointId.HandIndexTip,out var tip))continue;
                var p=notebookView.InverseTransformPoint(tip.position);
                NotebookButton3D touch=null;
                foreach(var item in notebookCaps)if(item.button.interactable&&item.rect.Contains(new Vector2(p.x,p.y))&&p.z>=-.015f&&p.z<=.008f){touch=item.button;break;}
                if(notebookPress.Step(this, hand, touch!=null)){PressLog("notebook button", hand);touch.Press();}
            }
        }
    }
}
