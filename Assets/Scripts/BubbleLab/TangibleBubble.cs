using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.BubbleLab
{
    // Shape owns vertices/collider, never the grabbed root pose or transform scale.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(BoxCollider))]
    public sealed class TangibleBubble : MonoBehaviour
    {
        public Text title, detail;
        public CanvasGroup ink;
        public RectTransform textPanel;
        public float compactWidth = .28f, openWidth = .52f;
        public float compactHeight = .18f, openHeight = .26f;
        public float squeezeDistance = .085f, restoreTravel = .09f;
        public float Width { get; private set; } = .28f;
        public float Crumple { get; private set; }
        public bool IsCrumpled { get; private set; }
        public bool IsOpen { get; private set; }
        public float Height => Mathf.Lerp(compactHeight, openHeight, Mathf.InverseLerp(compactWidth, openWidth, Width));
        public int Hands => grab == null || grab.GrabPoints == null ? 0 : grab.GrabPoints.Count;
        public string State => IsCrumpled ? "Crumpled" : IsOpen ? "Open" : Hands == 2 ? "Stretching" : Hands == 1 ? "Held" : "Ready";
        public int ShapeUpdates { get; private set; }
        private Mesh mesh;
        private Vector3[] vertices;
        private Vector2[] coords;
        private Vector3[] normals;
        private Vector4[] tangents;
        private int[] indices;
        private float[] pinWeights, curls, ripples;
        private Vector3[] foldAcross = { new Vector3(1,.18f,0).normalized, new Vector3(-1,.12f,0).normalized, new Vector3(.24f,1,0).normalized, new Vector3(-.18f,-1,0).normalized };
        private Vector3[] hinges = { new Vector3(.045f,0,0),new Vector3(-.043f,0,0),new Vector3(0,.027f,0),new Vector3(0,-.026f,0) };
        private Quaternion[] rotations = new Quaternion[4];
        private float drawnWidth=-1, drawnFold=-1, drawnCurl=-1;
        private bool wasHeld;
        private BoxCollider shape;
        private Grabbable grab;
        private float desiredWidth, desiredFold, widthSpeed, foldSpeed, curl, curlSpeed;
        private float startGap, startWidth;
        private bool startedCrumpled, squeezeArmed;
        private bool stickyNote;
        public bool IsStickyNote => stickyNote;
        private bool meetingPaper;
        // Toy style (main scene only): a rounded card with real thickness. The front grid
        // keeps every stretch/crumple behaviour; a back face and side band follow it.
        public float toyCornerRadius = .04f, toyThickness = .007f;
        public Color toyEdgeTint = new Color(.88f, .88f, .92f, 1f);
        public Color toyBackTint = new Color(.95f, .95f, .97f, 1f);
        public Color toyStripTint = new Color(.92f, .84f, .70f, 1f);
        public bool IsToyShape => toyShape;
        private bool toyShape;
        private Color[] colors;
        private int[] perimeter;
        private int gridCount, frontIndexCount;
        const int Columns = 32, Rows = 20;

        private void Awake()
        {
            BuildPreview();
        }
        public void BuildPreview()
        {
            shape = GetComponent<BoxCollider>(); grab = GetComponent<Grabbable>();
            Width = desiredWidth = compactWidth;
            IsOpen = IsCrumpled = false; Crumple = desiredFold = 0;
            Build(); RefreshShape();
        }
        public void BeginTwoHands(float distance)
        {
            if (stickyNote) return;
            startGap = distance; startWidth = Width; startedCrumpled = IsCrumpled;
            squeezeArmed = !IsCrumpled && distance > squeezeDistance + .045f;
        }
        public void MoveTwoHands(float distance)
        {
            if (stickyNote) return;
            if (IsCrumpled)
            {
                float restoring = Mathf.InverseLerp(startGap, startGap + restoreTravel, distance);
                desiredFold = 1 - restoring;
                if (restoring >= .999f)
                {
                    IsCrumpled = false; IsOpen = false;
                    desiredWidth = Mathf.Max(compactWidth, distance); startedCrumpled = false;
                    squeezeArmed = distance > squeezeDistance + .045f;
                }
                return;
            }
            desiredFold = Mathf.InverseLerp(compactWidth * .78f, squeezeDistance, distance);
            if (distance > squeezeDistance + .045f) squeezeArmed = true;
            if (squeezeArmed && distance < squeezeDistance)
            {
                IsCrumpled = true; IsOpen = false; desiredWidth = compactWidth; desiredFold = 1;
                startGap = distance; startedCrumpled = true;
                return;
            }
            if (!startedCrumpled) desiredWidth = Mathf.Clamp(distance, compactWidth, openWidth * 1.08f);
            if (desiredWidth > compactWidth * 1.3f) IsOpen = true;
            else if (desiredWidth < compactWidth * 1.12f) IsOpen = false;
        }
        public void OpenFully()
        {
            IsCrumpled = false;
            IsOpen = true;
            desiredFold = 0f;
            desiredWidth = openWidth;
        }
        public void CloseToCompact()
        {
            if (stickyNote || IsCrumpled) return;
            IsOpen = false;
            desiredFold = 0f;
            desiredWidth = compactWidth;
        }
        // Main-scene presentation opts in; standalone paper/foam/membrane labs stay intact.
        public void UseMeetingPaperShape()
        {
            meetingPaper = true;
            drawnWidth = -1f;
        }
        // BubbleLab's own folded paper (clipped corners, deep corner curl) vs the meeting's toy card.
        public bool PaperLook => !toyShape && !meetingPaper;
        public void UsePaperLook(bool on)
        {
            if (on)
            {
                if (!toyShape && !meetingPaper) return;
                toyShape = false;
                meetingPaper = false;
                if (shape == null) shape = GetComponent<BoxCollider>();
                if (grab == null) grab = GetComponent<Grabbable>();
                Build();
                drawnWidth = -1f;
                RefreshShape();
            }
            else if (!toyShape) UseToyShape();
        }

        public void UseToyShape()
        {
            if (toyShape) return;
            toyShape = true;
            meetingPaper = true;
            if (shape == null) shape = GetComponent<BoxCollider>();
            if (grab == null) grab = GetComponent<Grabbable>();
            Build();
            drawnWidth = -1f;
            RefreshShape();
        }
        // The lower-right corner of a sticky note lifts off the sheet a little.
        private static float StickyLift(float u, float v)
        {
            float t = Mathf.Clamp01(1f - (1f - u) * 3.4f - v * 3.4f);
            return t * t * (3f - 2f * t);
        }

        // Adhesive band at the top, a faint darkening toward the bottom and under the curl.
        private Color StickyColor(Vector2 uv)
        {
            if (uv.y > .87f) return toyStripTint;
            float shade = (1f - uv.y) * .3f + StickyLift(uv.x, uv.y) * .6f;
            return Color.Lerp(Color.white, new Color(.94f, .92f, .86f), shade);
        }

        public void UseStickyNoteShape()
        {
            stickyNote = true;
            toyThickness = .0025f; // paper, not a block
            compactWidth = .072f;
            compactHeight = .070f;
            openWidth = .30f;
            openHeight = .29f;
            Width = desiredWidth = compactWidth;
            IsOpen = IsCrumpled = false;
            Crumple = desiredFold = widthSpeed = foldSpeed = curl = curlSpeed = 0f;
            RefreshShape();
        }

        private void LateUpdate()
        {
            if (stickyNote || Hands < 2) { desiredWidth = IsOpen ? openWidth : compactWidth; desiredFold = IsCrumpled ? 1 : 0; }
            Width = Mathf.SmoothDamp(Width, desiredWidth, ref widthSpeed, Hands == 2 ? .035f : .16f);
            Crumple = Mathf.SmoothDamp(Crumple, desiredFold, ref foldSpeed, .065f);
            float stretch = Mathf.InverseLerp(compactWidth, openWidth, Width);
            curl = Mathf.SmoothDamp(curl, Hands == 1 ? .024f : Hands == 2 ? .018f * Mathf.Sin(stretch * Mathf.PI) : .005f, ref curlSpeed, .12f);
            int hands=stickyNote ? 0 : Hands;
            if(hands>0||wasHeld||Mathf.Abs(Width-drawnWidth)>.00003f||Mathf.Abs(Crumple-drawnFold)>.0002f||Mathf.Abs(curl-drawnCurl)>.00002f) RefreshShape();
            wasHeld=hands>0;
            if (ink != null) ink.alpha = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.08f, .35f, Crumple));
            if (textPanel != null) textPanel.sizeDelta = new Vector2((Width - .04f) * 1000, 150);
            if (detail != null) detail.gameObject.SetActive(IsOpen && Crumple < .1f);
        }
        private void Build()
        {
            int grid = (Columns + 1) * (Rows + 1);
            gridCount = grid;
            int rim = toyShape ? 2 * (Columns + Rows) : 0;
            int total = toyShape ? grid * 2 + rim * 2 : grid;
            coords = new Vector2[total]; vertices = new Vector3[total];
            for (int y = 0; y <= Rows; y++) for (int x = 0; x <= Columns; x++) coords[y*(Columns+1)+x] = new Vector2((float)x/Columns, (float)y/Rows);
            var triangles = new List<int>();
            for(int y=0;y<Rows;y++) for(int x=0;x<Columns;x++)
            {
                int a=y*(Columns+1)+x, b=a+1, c=a+Columns+1, d=c+1;
                triangles.AddRange(new[]{a,c,b,b,c,d});
            }
            frontIndexCount = triangles.Count;
            if (toyShape) BuildToySlab(triangles, grid, rim);
            indices=triangles.ToArray();normals=new Vector3[vertices.Length];tangents=new Vector4[vertices.Length];
            pinWeights=new float[grid];curls=new float[grid];ripples=new float[grid];
            for(int i=0;i<grid;i++)
            {
                float u=coords[i].x,v=coords[i].y;
                pinWeights[i]=Mathf.Pow(Mathf.Abs(u-.5f)*2,10)*Mathf.Exp(-Mathf.Pow((v-.5f)*8,2));
                curls[i]=Mathf.Pow(Mathf.Abs(u-.5f)*2,3)+.4f*Mathf.Sin(v*5);
                ripples[i]=.004f*Mathf.Sin(u*23+v*9)*Mathf.Sin(v*19-u*5);
            }
            if (mesh == null)
            {
                mesh = new Mesh { name = "TangibleBubble_RuntimePaper" };
                mesh.MarkDynamic();
            }
            else mesh.Clear();
            mesh.vertices=vertices; mesh.uv=coords; mesh.triangles=indices;
            if (toyShape) { mesh.name = "TangibleBubble_RuntimeToyCard"; mesh.colors = colors; }
            GetComponent<MeshFilter>().sharedMesh=mesh;
        }
        // Back face (reversed winding) plus a closed side band around the grid perimeter.
        private void BuildToySlab(List<int> triangles, int grid, int rim)
        {
            perimeter = new int[rim];
            int k = 0;
            for (int x = 0; x < Columns; x++) perimeter[k++] = x;
            for (int y = 0; y < Rows; y++) perimeter[k++] = y * (Columns + 1) + Columns;
            for (int x = Columns; x > 0; x--) perimeter[k++] = Rows * (Columns + 1) + x;
            for (int y = Rows; y > 0; y--) perimeter[k++] = y * (Columns + 1);
            for (int i = 0; i < grid; i++) coords[grid + i] = coords[i];
            int side = grid * 2;
            for (int i = 0; i < rim; i++) coords[side + i] = coords[side + rim + i] = coords[perimeter[i]];
            for (int t = 0; t < frontIndexCount; t += 3)
            {
                triangles.Add(grid + triangles[t]);
                triangles.Add(grid + triangles[t + 2]);
                triangles.Add(grid + triangles[t + 1]);
            }
            for (int i = 0; i < rim; i++)
            {
                int f0 = side + i, f1 = side + (i + 1) % rim, b0 = side + rim + i, b1 = side + rim + (i + 1) % rim;
                triangles.AddRange(new[] { f0, f1, b0, f1, b1, b0 });
            }
            colors = new Color[vertices.Length];
        }
        // Maps the rectangular grid onto a rounded rectangle; corner cells are pulled onto
        // a quarter disc with a smooth square-to-disc mapping so the silhouette stays round.
        private static void RoundCorner(ref float x, ref float y, float halfWidth, float halfHeight, float radius)
        {
            float cx = halfWidth - radius, cy = halfHeight - radius;
            float ax = Mathf.Abs(x), ay = Mathf.Abs(y);
            if (radius <= 0f || ax <= cx || ay <= cy) return;
            float a = (ax - cx) / radius, b = (ay - cy) / radius;
            float ma = a * Mathf.Sqrt(Mathf.Max(0f, 1f - b * b * .5f));
            float mb = b * Mathf.Sqrt(Mathf.Max(0f, 1f - a * a * .5f));
            x = Mathf.Sign(x) * (cx + ma * radius);
            y = Mathf.Sign(y) * (cy + mb * radius);
        }
        private void FillToySlab(ref Vector3 minimum, ref Vector3 maximum)
        {
            int grid = gridCount, rim = perimeter.Length, side = grid * 2;
            Vector3 center = (minimum + maximum) * .5f;
            for (int i = 0; i < grid; i++)
            {
                Vector3 back = vertices[i] - normals[i] * toyThickness;
                vertices[grid + i] = back;
                normals[grid + i] = -normals[i];
                tangents[grid + i] = tangents[i];
                colors[i] = stickyNote ? StickyColor(coords[i]) : Color.white;
                colors[grid + i] = stickyNote ? toyStripTint : toyBackTint;
                minimum = Vector3.Min(minimum, back); maximum = Vector3.Max(maximum, back);
            }
            for (int i = 0; i < rim; i++)
            {
                int g = perimeter[i];
                Vector3 along = vertices[perimeter[(i + 1) % rim]] - vertices[perimeter[(i - 1 + rim) % rim]];
                Vector3 outward = Vector3.Cross(normals[g], along);
                if (outward.sqrMagnitude < 1e-12f) outward = vertices[g] - center;
                outward.Normalize();
                if (Vector3.Dot(outward, vertices[g] - center) < 0f) outward = -outward;
                vertices[side + i] = vertices[g];
                vertices[side + rim + i] = vertices[grid + g];
                normals[side + i] = normals[side + rim + i] = outward;
                tangents[side + i] = tangents[side + rim + i] = tangents[g];
                colors[side + i] = colors[side + rim + i] = stickyNote ? toyStripTint * toyEdgeTint : toyEdgeTint;
            }
        }
        private void RefreshShape()
        {
            float stretch = Mathf.InverseLerp(compactWidth,openWidth,Width);
            float height = Mathf.Lerp(compactHeight,openHeight,stretch);
            int hands=stickyNote ? 0 : Hands;
            Vector3 left=Vector3.zero,right=Vector3.zero;
            if(hands==2)
            {
                Vector3 a=transform.InverseTransformPoint(grab.GrabPoints[0].position),b=transform.InverseTransformPoint(grab.GrabPoints[1].position);
                left=a.x<b.x?a:b;right=a.x<b.x?b:a;
            }
            float fold=Crumple;
            rotations[0]=Quaternion.AngleAxis(133*fold,Vector3.Cross(foldAcross[0],Vector3.forward));
            rotations[1]=Quaternion.AngleAxis(-142*fold,Vector3.Cross(foldAcross[1],Vector3.forward));
            rotations[2]=Quaternion.AngleAxis(145*fold,Vector3.Cross(foldAcross[2],Vector3.forward));
            rotations[3]=Quaternion.AngleAxis(-151*fold,Vector3.Cross(foldAcross[3],Vector3.forward));
            float tension=.13f*Mathf.Sin(stretch*Mathf.PI);
            Vector3 cornerAcross=new Vector3(1,1,0).normalized;
            Quaternion cornerTurn=Quaternion.AngleAxis((meetingPaper ? -28f : -105f)*(1-fold),Vector3.Cross(cornerAcross,Vector3.forward));
            Vector3 minimum=Vector3.one*float.PositiveInfinity,maximum=Vector3.one*float.NegativeInfinity;
            for(int i=0;i<gridCount;i++)
            {
                float u=coords[i].x, v=coords[i].y;
                if (toyShape)
                {
                    float halfWidth = Width * .5f, halfHeight = height * .5f;
                    float radius = stickyNote ? Mathf.Min(Width, height) * .03f : Mathf.Min(toyCornerRadius, Mathf.Min(halfWidth, halfHeight) * .9f);
                    float tx = (u - .5f) * Width, ty = (v - .5f) * height;
                    RoundCorner(ref tx, ref ty, halfWidth, halfHeight, radius);
                    if (!stickyNote) ty *= 1 - tension * (1 - Mathf.Abs(u - .5f) * 2);
                    var toy = new Vector3(tx, ty, stickyNote ? -StickyLift(u, v) * Width * .06f : curl * curls[i]);
                    if (fold > .0001f) for (int f = 0; f < 4; f++) toy = Fold(toy, hinges[f], foldAcross[f], rotations[f]);
                    toy.z += fold * ripples[i];
                    if (hands == 2 && v >= 0) toy = Vector3.Lerp(toy, u < .5f ? left : right, pinWeights[i]);
                    vertices[i] = toy;
                    minimum = Vector3.Min(minimum, toy); maximum = Vector3.Max(maximum, toy);
                    continue;
                }
                if (stickyNote)
                {
                    // A square adhesive edge and a free lower-left corner lifting off the sheet.
                    float lift = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f - u * 1.35f - v * 4.4f));
                    var sticky = new Vector3((u - .5f) * Width, (v - .5f + .17f * lift) * height, -.10f * Width * lift);
                    vertices[i] = sticky;
                    minimum = Vector3.Min(minimum, sticky);
                    maximum = Vector3.Max(maximum, sticky);
                    continue;
                }
                float y=(v-.5f)*height;
                // Straight clipped corners match the folded paper-card silhouette. Slightly
                // asymmetric cuts keep the result handmade without making the text panel tilt.
                float corner=meetingPaper ? Mathf.Lerp(.008f,.012f,stretch) : Mathf.Lerp(.018f,.026f,stretch);
                float edgeDistance=height*.5f-Mathf.Abs(y);
                float cut=Mathf.Clamp(corner-edgeDistance,0,corner);
                float leftCut=cut*(y>0?1f:.82f);
                float rightCut=cut*(y>0?.78f:1f);
                float x=Mathf.Lerp(-Width*.5f+leftCut,Width*.5f-rightCut,u);
                // Stretch produces a gentle waist between the two side grips, then relaxes when open.
                y *= 1 - tension * (1-Mathf.Abs(u-.5f)*2);
                var p=new Vector3(x,y,curl*curls[i]);
                // A small turned corner catches light like a thin folded sheet.
                if(fold<.99f)p=Fold(p,new Vector3(Width*.43f,height*.37f,0),cornerAcross,cornerTurn);
                // Fold the actual sheet over four oblique hinges; never morph into a sphere.
                if(fold>.0001f) for(int f=0;f<4;f++)p=Fold(p,hinges[f],foldAcross[f],rotations[f]);
                p.z += fold * ripples[i];
                if (hands == 2 && v >= 0)
                {
                    p=Vector3.Lerp(p,u<.5f?left:right,pinWeights[i]);
                }
                vertices[i]=p;
                minimum=Vector3.Min(minimum,p);maximum=Vector3.Max(maximum,p);
            }
            System.Array.Clear(normals,0,normals.Length);
            for(int t=0;t<frontIndexCount;t+=3)
            {
                int a=indices[t],b=indices[t+1],c=indices[t+2];
                Vector3 n=Vector3.Cross(vertices[b]-vertices[a],vertices[c]-vertices[a]);
                normals[a]+=n;normals[b]+=n;normals[c]+=n;
            }
            for(int i=0;i<gridCount;i++)
            {
                normals[i].Normalize();
                if(fold>.5f&&Vector3.Dot(normals[i],vertices[i])<0)normals[i]=-normals[i];
                Vector3 tangent=Vector3.right-normals[i]*normals[i].x;
                if(tangent.sqrMagnitude<.0001f)tangent=Vector3.Cross(Vector3.forward,normals[i]);
                tangent.Normalize();tangents[i]=new Vector4(tangent.x,tangent.y,tangent.z,-1);
            }
            if(toyShape)FillToySlab(ref minimum,ref maximum);
            mesh.SetVertices(vertices);mesh.SetNormals(normals);mesh.SetTangents(tangents);
            if(toyShape)mesh.SetColors(colors);
            var bounds=new Bounds((minimum+maximum)*.5f,maximum-minimum);mesh.bounds=bounds;shape.center=bounds.center;
            shape.size=new Vector3(Mathf.Max(.065f,bounds.size.x),Mathf.Max(.065f,bounds.size.y),Mathf.Max(.035f,bounds.size.z));
            drawnWidth=Width;drawnFold=Crumple;drawnCurl=curl;ShapeUpdates++;
        }
        private static Vector3 Fold(Vector3 p, Vector3 hinge, Vector3 across, Quaternion rotation)
        {
            float d=Vector3.Dot(p-hinge,across);
            if(d<=0) return p;
            Vector3 pivot=p-across*d;
            return pivot+rotation*(across*d);
        }
        private void OnDestroy() { if(mesh != null) Destroy(mesh); }
    }
}
