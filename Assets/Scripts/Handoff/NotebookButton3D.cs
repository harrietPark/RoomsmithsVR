using System;
using System.Collections;
using UnityEngine;

namespace Windups.Handoff
{
    // Raised cap and recessed foot share the notebook's pose. The existing pinch/ray
    // hit routing and a near fingertip press both call the same action.
    public sealed class NotebookButton3D : MonoBehaviour
    {
        public Transform cap;
        public bool interactable;
        public Action clicked;
        public Mesh footMesh, capMesh;
        private bool pressing;
        public void Press()
        {
            if(!interactable||pressing||!isActiveAndEnabled) return;
            StartCoroutine(PressAndClick());
        }
        private IEnumerator PressAndClick()
        {
            pressing=true;
            cap.localPosition=Vector3.forward*.0018f;
            ToySounds.Play(ToySounds.Cue.Paper,transform.position,.25f,1.8f);
            yield return new WaitForSeconds(.07f);
            clicked?.Invoke();
            if(cap!=null)cap.localPosition=Vector3.zero;
            pressing=false;
        }
        private void OnDestroy()
        {
            if(footMesh!=null)Destroy(footMesh);
            if(capMesh!=null)Destroy(capMesh);
        }
    }
}
