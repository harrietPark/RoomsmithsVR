using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    public sealed partial class MakerSession
    {
        // The grip is below the neck, clear of the lens. It meets the visible mitten's
        // centre, not the wrist bone (which is 11.5 mm below that centre in this model).
        private static readonly Vector3 MagnifierHandle = new Vector3(0f, -.042f, 0f);
        public const float MagnifierRadius = .0185f;
        private Vector3 magnifierPalmLocal;
        private Transform magnifierHand;
        public Vector3 MagnifierPalm => magnifierHand != null ? magnifierHand.TransformPoint(magnifierPalmLocal) : Vector3.zero;

        private void BuildMagnifier()
        {
            if (magnifier != null) return; // (F55: the tools can be built before the session begins)
            magnifier = new GameObject("Magnifier").transform;
            magnifier.SetParent(transform, false);
            var brass = Resources.Load<Material>("MakerMagnifierBrass");
            var grip = Resources.Load<Material>("MakerMagnifierGrip");
            var glass = Resources.Load<Material>("MakerMagnifierGlass");
            var rim = ToyMeshes.Torus(.12f, "Brushed brass rim");
            var ring = MagnifierPart("Brass rim", rim, brass, Vector3.zero);
            ring.localScale = Vector3.one * .0165f;
            var inset = ToyMeshes.Torus(.035f, "Inner lens seat");
            var seat = MagnifierPart("Lens seat", inset, grip, Vector3.zero);
            seat.localScale = Vector3.one * .0148f;
            MagnifierPart("Glass lens", MagnifierLens(), glass, Vector3.zero);
            MagnifierPart("Brass neck", MagnifierLathe(new[] {
                new Vector2(.0028f,-.016f),new Vector2(.0035f,-.020f),new Vector2(.0035f,-.028f)
            },"Neck"), brass, Vector3.zero);
            MagnifierPart("Soft teal handle", MagnifierLathe(new[] {
                new Vector2(0,-.025f),new Vector2(.0035f,-.025f),new Vector2(.0047f,-.028f),
                new Vector2(.0048f,-.050f),new Vector2(.0043f,-.054f),new Vector2(.002f,-.056f),new Vector2(0,-.057f)
            },"Grip"), grip, Vector3.zero);
            // A small brass end cap makes the lower end readable below the mitten.
            MagnifierPart("Handle end cap", MagnifierLathe(new[] {
                new Vector2(.00435f,-.053f),new Vector2(.00435f,-.054f),new Vector2(.002f,-.0562f),new Vector2(0,-.0565f)
            },"End cap"), brass, Vector3.zero);
            magnifier.gameObject.AddComponent<NotebookPropFollower>();
            magnifier.gameObject.SetActive(false);
        }

        private Transform MagnifierPart(string name, Mesh mesh, Material look, Vector3 at)
        {
            owned.Add(mesh);
            var part = Part(name,mesh); part.SetParent(magnifier,false); part.localPosition=at;
            var renderer=part.GetComponent<MeshRenderer>(); renderer.sharedMaterial=look;
            renderer.receiveShadows=false;
            return part;
        }

        private void CacheMagnifierPalm(WindupBody body, Transform hand)
        {
            if(magnifierHand==hand) return;
            magnifierHand=hand;
            foreach(var skin in body.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if(skin.name!="Hand_R") continue;
                var mesh=new Mesh(); skin.BakeMesh(mesh,true);
                Vector3 min=Vector3.positiveInfinity,max=Vector3.negativeInfinity;
                foreach(var v in mesh.vertices) {
                    var p=hand.InverseTransformPoint(skin.transform.TransformPoint(v));
                    min=Vector3.Min(min,p);max=Vector3.Max(max,p);
                }
                if(mesh.vertexCount>0) magnifierPalmLocal=(min+max)*.5f;
                Destroy(mesh); break;
            }
        }

        private static Mesh MagnifierLathe(Vector2[] profile,string name)
        {
            const int segments=32;
            var v=new List<Vector3>();var uv=new List<Vector2>();var tris=new List<int>();
            for(int row=0;row<profile.Length;row++) for(int s=0;s<=segments;s++) {
                float a=s*Mathf.PI*2/segments;
                v.Add(new Vector3(Mathf.Cos(a)*profile[row].x,profile[row].y,Mathf.Sin(a)*profile[row].x));
                uv.Add(new Vector2((float)s/segments,profile[row].y*100));
                if(row==0||s==segments)continue;
                int p=row*(segments+1)+s,q=p-segments-1;
                tris.AddRange(new[]{q,q+1,p,q+1,p+1,p});
            }
            var mesh=new Mesh {name="Magnifier "+name};mesh.SetVertices(v);mesh.SetUVs(0,uv);mesh.SetTriangles(tris,0);mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }

        private static Mesh MagnifierLens()
        {
            const int rings=7,segments=48;
            var v=new List<Vector3>();var normals=new List<Vector3>();var uv=new List<Vector2>();var tris=new List<int>();
            for(int face=0;face<2;face++) {
                int start=v.Count;float sign=face==0?-1:1;
                for(int r=0;r<=rings;r++) for(int s=0;s<=segments;s++) {
                    float t=(float)r/rings,a=s*Mathf.PI*2/segments;
                    float x=Mathf.Cos(a)*t,y=Mathf.Sin(a)*t;
                    v.Add(new Vector3(x*.0147f,y*.0147f,sign*(.0004f+.0012f*(1-t*t))));
                    normals.Add(new Vector3(x*.16f,y*.16f,sign).normalized);uv.Add(new Vector2(x,y)*.5f+Vector2.one*.5f);
                    if(r==0||s==segments)continue;
                    int p=start+r*(segments+1)+s,q=p-segments-1;
                    if(face==0)tris.AddRange(new[]{q,q+1,p,q+1,p+1,p});else tris.AddRange(new[]{q,p,q+1,q+1,p,p+1});
                }
            }
            var mesh=new Mesh {name="Convex glass lens"};mesh.SetVertices(v);mesh.SetNormals(normals);mesh.SetUVs(0,uv);mesh.SetTriangles(tris,0);mesh.RecalculateBounds();return mesh;
        }
    }
}
