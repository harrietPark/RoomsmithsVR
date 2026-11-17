using Oculus.Interaction;
using UnityEngine;

namespace Windups.BubbleLab
{
    // ISDK supplies real hand grab poses; this transformer adds paper deformation to pose following.
    public sealed class BubbleGrabTransformer : MonoBehaviour, ITransformer
    {
        private IGrabbable target;
        private TangibleBubble bubble;
        private Vector3 offset, initialAxis;
        private Quaternion rotation;
        public void Initialize(IGrabbable grabbable) { target=grabbable; bubble=GetComponent<TangibleBubble>(); }
        public void BeginTransform()
        {
            var points=target.GrabPoints;
            if(points.Count==1)
            {
                offset=Quaternion.Inverse(points[0].rotation)*(target.Transform.position-points[0].position);
                rotation=Quaternion.Inverse(points[0].rotation)*target.Transform.rotation;
            }
            else if(points.Count>=2)
            {
                initialAxis=points[1].position-points[0].position;
                offset=target.Transform.position-(points[0].position+points[1].position)*.5f;
                rotation=target.Transform.rotation;
                bubble.BeginTwoHands(initialAxis.magnitude);
            }
        }
        public void UpdateTransform()
        {
            var points=target.GrabPoints;
            if(points.Count==1)
                target.Transform.SetPositionAndRotation(points[0].position+points[0].rotation*offset,points[0].rotation*rotation);
            else if(points.Count>=2)
            {
                Vector3 axis=points[1].position-points[0].position;
                Quaternion delta=axis.sqrMagnitude>.0001f&&initialAxis.sqrMagnitude>.0001f?Quaternion.FromToRotation(initialAxis,axis):Quaternion.identity;
                target.Transform.SetPositionAndRotation((points[0].position+points[1].position)*.5f+delta*offset,delta*rotation);
                bubble.MoveTwoHands(axis.magnitude);
            }
        }
        public void EndTransform() { }
    }
}
