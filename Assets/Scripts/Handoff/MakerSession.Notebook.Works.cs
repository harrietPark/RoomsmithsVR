using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    public sealed partial class MakerSession
    {
        // F34: immutable project association; old event notes remain readable and keep their file format.
        [Serializable] public sealed class NotebookWork
        {
            public string id, name, at, photo, reference, look, sketch, measured, dimensions, shape, stl, colourFile;
            public string baseHex;
            public string topic, completedOn, glb, fbx, obj, pickupCode, pickupUrl;
            public bool completed;
            public bool finalNameReady;
            public List<string> finishedPhotos = new List<string>();
            public List<JourneyPlacement> journeyPlaces = new List<JourneyPlacement>();
            public List<Swatch> swatches = new List<Swatch>();
        }
        public readonly List<NotebookWork> NotebookWorks = new List<NotebookWork>();
        private int IndexSpreads => Mathf.Max(1, (NotebookWorks.Count + 5) / 6);
        private readonly Dictionary<Transform, string> notebookOwners = new Dictionary<Transform, string>();
        private readonly Dictionary<Transform, float> notebookPending = new Dictionary<Transform, float>();
        private readonly List<GameObject> notebookWidgets = new List<GameObject>();
        private readonly List<(Rect rect, Action click)> notebookButtons = new List<(Rect, Action)>();
        private readonly Color notebookInk = new Color(.17f, .19f, .23f);

        private void SaveNotebook()
        {
            try { WriteNotebookAtomic(); }
            catch (Exception e) { Debug.LogWarning("[Windups Notebook] " + e.Message); }
        }

        private void MigrateNotebook()
        {
            LoadNotes();
            foreach (var group in Notes.GroupBy(n => n.project))
            {
                if (NotebookWorks.Any(w => w.id == group.Key)) continue;
                var made = group.LastOrDefault(n => n.kind == "made");
                if (made == null) continue; // unfinished conversations are not makes
                NotebookWorks.Add(new NotebookWork { id = group.Key, name = made.title.Replace("Made: ", ""), at = made.at,
                    look = group.LastOrDefault(n => n.kind == "look")?.image,
                    sketch = group.LastOrDefault(n => n.title == "Your outline" || n.title == "Your side shape")?.image,
                    measured = group.LastOrDefault(n => n.kind == "measure")?.text });
            }
            NotebookWorks.Sort((a, b) => string.CompareOrdinal(b.id, a.id));
        }

        private void QueueNotebookSnapshot(Transform thing)
        {
            if (thing == null || thing == TestCube) return;
            LoadNotes();
            if (thing == Product && project != null) notebookOwners[thing] = project;
            if (!notebookOwners.ContainsKey(thing)) return;
            notebookPending[thing] = Time.unscaledTime + .65f; // settle the build/paint, capture once
        }
        private void NotebookSnapshotFrame()
        {
            foreach (var item in notebookPending.ToArray())
                if (Time.unscaledTime >= item.Value) { notebookPending.Remove(item.Key); CaptureNotebookWork(item.Key); }
        }
        private void FlushNotebookSnapshot()
        {
            foreach (var thing in notebookPending.Keys.ToArray()) CaptureNotebookWork(thing, true);
            notebookPending.Clear();
        }
        // F57: `now` (a flush on leaving) takes the photo at once; otherwise it is read back from the GPU and packed off the
        // main thread (the old ReadPixels + EncodeToJPG, and an STL of a rebuilt view, cost a visible frame).
        private void CaptureNotebookWork(Transform thing, bool now = false)
        {
            if (thing == null || !notebookOwners.TryGetValue(thing, out var id)) return;
            var work = NotebookWorks.FirstOrDefault(w => w.id == id);
            bool current = thing == Product;
            if (work == null)
            {
                if (!current || Design == null) return;
                work = new NotebookWork { id = id, at = DateTime.Now.ToString("dd MMM yyyy") };
                NotebookWorks.Insert(0, work);
            }
            Directory.CreateDirectory(NotesFolder);
            string stem = Path.Combine(NotesFolder, id);
            try
            {
                if (current)
                {
                    work.name = MakerRoles.Clean(Design.name);
                    work.baseHex = "#"+ColorUtility.ToHtmlStringRGB(Design.Tint(new Color(.95f,.85f,.62f)));
                    work.measured = HasMeasure ? (TargetName ?? "Object") + " " + Measured.ToString("0.#") + " mm" : null;
                    work.shape = string.IsNullOrWhiteSpace(ShapeWords) ? (string.IsNullOrWhiteSpace(Shape) ? Design.shape : Shape) : ShapeWords;
                    work.reference = SaveWorkImage(stem + "_reference.jpg", ShapeRef ?? Target?.crop ?? Reference);
                    work.look = SaveWorkImage(stem + "_look.jpg", LookJpeg);
                    work.sketch = SaveWorkImage(stem + "_sketch.jpg", DrawingJpeg);
                    // F87 (the user: "only the colours I used"): the swatches the picked finish named; the paint on it is added below.
                    var used = FinishUsedHexes();
                    work.swatches = Notes.Where(n => n.project == id && n.kind == "palette" && (n.title.StartsWith("Colour:") || n.title.StartsWith("Texture:")))
                        .Select(n => new Swatch { name = n.title, kind = n.title.StartsWith("Texture:") ? "texture" : "colour", image = n.image,
                            hex = System.Text.RegularExpressions.Regex.Match(n.text ?? "", "#[0-9a-fA-F]{6}").Value })
                        .Where(s => used != null && used.Contains(s.hex.ToLowerInvariant()))
                        .GroupBy(s => s.hex.ToLowerInvariant() + "|" + s.kind).Select(g => g.First()).ToList();
                }
                work.swatches.RemoveAll(w => w.at == "paint");
                var painted = thing.GetComponentInChildren<PaintableThing>();
                if (painted != null) foreach (var mark in painted.SavedMarks())
                    if (!work.swatches.Any(w => w.hex == mark.hex && w.image == mark.image))
                        work.swatches.Add(new Swatch { kind = mark.kind, hex = mark.hex, image = mark.image, name = mark.part, at = "paint" });
                var filter = thing.GetComponentInChildren<MeshFilter>();
                // F57: a model's print files are the closed solid that "Print it" writes (AttachPrintToNotebook), never its view
                bool model = (current ? Design?.kind : madeDesigns.TryGetValue(thing, out var made) ? made?.kind : null) == "model";
                if (filter != null && filter.sharedMesh != null && model)
                {
                    Vector3 size = Vector3.Scale(filter.sharedMesh.bounds.size, filter.transform.lossyScale) * 1000f;
                    work.dimensions = $"{size.x:0.#} x {size.z:0.#} x {size.y:0.#} mm";
                    if (work.stl != null && !work.stl.Contains("prints")) { work.stl = null; work.colourFile = null; } // an old view STL
                }
                else if (filter != null && filter.sharedMesh != null)
                {
                    Vector3 size = Vector3.Scale(filter.sharedMesh.bounds.size, filter.transform.lossyScale) * 1000f;
                    work.dimensions = $"{size.x:0.#} x {size.z:0.#} x {size.y:0.#} mm";
                    work.stl = stem + ".stl";
                    File.WriteAllBytes(work.stl, CoasterMesh.Stl(filter.sharedMesh, filter.transform.lossyScale.x, work.name));
                    if (painted != null && painted.Mesh != null && painted.Parts != null)
                    {
                        var colours = new List<Color>();
                        for(int i=0;i<painted.Parts.Length;i++)
                            colours.Add(painted.Marks.TryGetValue(i,out var mark) && ColorUtility.TryParseHtmlString(mark.hex ?? "",out var c)?c:Hex(work.baseHex ?? "#f2d99e"));
                        work.colourFile=stem+".3mf";
                        File.WriteAllBytes(work.colourFile,FitShapes.ThreeMf(painted.Mesh,thing.localScale.x,painted.Parts,colours,work.name));
                    }
                }
                if (work.completed && !string.IsNullOrEmpty(work.photo)) { SaveNotebook(); return; }
                work.photo = stem + "_made.jpg";
                if (now || !SystemInfo.supportsAsyncGPUReadback) NotebookPhoto(thing, work.photo);
                else StartCoroutine(NotebookPhotoLater(thing, work.photo));
                SaveNotebook();
                if (NotebookOpen) FillSpread();
            }
            catch (Exception e) { Debug.LogWarning("[Windups Notebook] snapshot: " + e.Message); }
        }
        public static int BlankPhotosNotWritten { get; private set; }
        private static string SaveWorkImage(string path, byte[] jpeg)
        { if (jpeg == null || jpeg.Length == 0) return null; File.WriteAllBytes(path, jpeg); return path; }

        private static void BuildNotebookPhoto(GameObject stage, GameObject cameraGo, Transform thing, RenderTexture target)
        {
            // Photograph the finished surface exported to GLB, excluding tags and transient brush overlays.
            var paint = thing.GetComponentInChildren<PaintableThing>();
            var source = paint != null ? paint.Renderer : thing.GetComponentInChildren<MeshRenderer>();
            if (source == null) throw new InvalidOperationException("No product renderer for photo.");
            var filter = source.GetComponent<MeshFilter>();
            var copy = new GameObject("Product", typeof(MeshFilter), typeof(MeshRenderer));
            copy.transform.SetParent(stage.transform, false);
            copy.layer = 31;
            // Recenter before rendering. A -1000m offset loses precision in small painted surfaces.
            copy.transform.SetPositionAndRotation(source.transform.position - source.bounds.center, source.transform.rotation);
            copy.transform.localScale = source.transform.lossyScale;
            copy.GetComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            var renderer = copy.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = source.sharedMaterials;
            var properties = new MaterialPropertyBlock();
            source.GetPropertyBlock(properties);
            renderer.SetPropertyBlock(properties);
            var bounds = renderer.bounds;
            var camera = cameraGo.AddComponent<Camera>();
            camera.enabled = false;
            camera.cullingMask = 1 << 31;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.94f, .92f, .88f);
            camera.orthographic = true;
            camera.orthographicSize = Mathf.Max(.03f, bounds.extents.magnitude * 1.15f);
            camera.nearClipPlane = .1f;
            camera.farClipPlane = 4f;
            camera.transform.position = bounds.center + new Vector3(.4f, .6f, -.7f).normalized;
            camera.transform.LookAt(bounds.center);
            camera.targetTexture = target;
        }

        // Only duplicate the product's renderers: no camera/passthrough, room, scripts or private surroundings.
        private static void NotebookPhoto(Transform thing, string path)
        {
            var stage = new GameObject("Notebook Photo");
            var cameraGo = new GameObject("Notebook Photo Camera");
            var rt = RenderTexture.GetTemporary(1024, 1024, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Texture2D image = null;
            try
            {
                BuildNotebookPhoto(stage, cameraGo, thing, rt);
                cameraGo.GetComponent<Camera>().Render();
                RenderTexture.active = rt; image = new Texture2D(1024,1024,TextureFormat.RGB24,false);
                image.ReadPixels(new Rect(0,0,1024,1024),0,0); image.Apply();
                if (PhotoBlank(image.GetPixels32(), 1024, 1024)) BlankPhotosNotWritten++; // F104: nothing in it, not kept
                else File.WriteAllBytes(path,image.EncodeToJPG(90));
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); Destroy(stage); Destroy(cameraGo); if (image != null) Destroy(image); }
        }

        // F57: the same photo, read back from the GPU and packed as a JPEG off the main thread.
        private System.Collections.IEnumerator NotebookPhotoLater(Transform thing, string path)
        {
            if (readbackRows == 0) yield return CheckRows();
            if (readbackRows < 0 || thing == null) { if (thing != null) NotebookPhoto(thing, path); yield break; }
            var stage = new GameObject("Notebook Photo");
            var cameraGo = new GameObject("Notebook Photo Camera");
            var rt = RenderTexture.GetTemporary(1024, 1024, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            UnityEngine.Rendering.AsyncGPUReadbackRequest request;
            try
            {
                BuildNotebookPhoto(stage, cameraGo, thing, rt);
                cameraGo.GetComponent<Camera>().Render();
                request = UnityEngine.Rendering.AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            }
            finally { Destroy(stage); Destroy(cameraGo); }
            float until = Time.realtimeSinceStartup + 3f;
            while (!request.done && Time.realtimeSinceStartup < until) yield return null;
            RenderTexture.ReleaseTemporary(rt);
            if (!request.done || request.hasError) yield break;
            var data = request.GetData<Color32>();
            if (data.Length != 1024 * 1024) yield break;
            var pixels = new Color32[1024 * 1024];
            data.CopyTo(pixels);
            bool flip = readbackRows == 2;
            // F104: a photo with nothing in it (the render showed nothing) is not written; the work keeps the photo it had.
            var write = System.Threading.Tasks.Task.Run(() =>
            {
                if (PhotoBlank(pixels, 1024, 1024)) return false;
                File.WriteAllBytes(path, RoomEyes.Jpeg(flip ? FlipRows(pixels, 1024) : pixels, 1024, 1024, 90));
                return true;
            });
            while (!write.IsCompleted) yield return null;
            if (write.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && !write.Result)
            { BlankPhotosNotWritten++; Debug.LogWarning("[Windups Notebook] the photo came out blank; not kept: " + Path.GetFileName(path)); }
            if (NotebookOpen) FillSpread();
        }

        // F57: a model's closed print files go into its notebook work (the notebook's export sends these).
        private void AttachPrintToNotebook(Transform thing, string stl, string colourFile)
        {
            if (thing == null || !notebookOwners.TryGetValue(thing, out var id)) return;
            var work = NotebookWorks.FirstOrDefault(w => w.id == id);
            if (work == null) return;
            work.stl = stl;
            work.colourFile = colourFile;
            SaveNotebook();
        }

        public void SelectNotebookWork(int index)
        { if (index < 0 || index >= NotebookWorks.Count) return; NotebookSpread = IndexSpreads + index; FillSpread(); }

        private void FillSpread()
        {
            if (notebookView == null) return;
            MigrateNotebook();
            NotebookSpread = Mathf.Clamp(NotebookSpread, 0, NotebookSpreads - 1);
            foreach (var go in notebookWidgets) if (go != null) { go.SetActive(false); Destroy(go); }
            notebookWidgets.Clear(); notebookButtons.Clear(); notebookCaps.Clear();
            foreach (var t in noteTextures) if (t != null) Destroy(t);
            noteTextures.Clear();
            for (int i=0;i<2;i++) { pageTitles[i].text=""; pageBodies[i].text=""; pageFooters[i].text=""; pageImages[i].gameObject.SetActive(false); }
            if (NotebookSpread < IndexSpreads)
            {
                TextOnPage(0, "Our makes", 8, 5, 134, 18, 14);
                TextOnPage(1, "Pick a make", 8, 5, 134, 18, 12);
                if (NotebookWorks.Count == 0) TextOnPage(0,"Your first make will go here.",8,45,134,45,11);
                for (int row=0;row<6;row++)
                {
                    int index=NotebookSpread*6+row; if(index>=NotebookWorks.Count) break;
                    var w=NotebookWorks[index]; int page=row/3; float y=29+(row%3)*44;
                    PictureOnPage(page,w.photo,8,y,38,38);
                    TextOnPage(page,w.name,50,y,92,23,10);
                    TextOnPage(page,w.at,50,y+24,92,12,7);
                    AddNotebookHit(page,8,y,134,40,()=>SelectNotebookWork(index));
                }
            }
            else
            {
                var w=NotebookWorks[NotebookSpread-IndexSpreads];
                TextOnPage(0,w.name,8,5,134,21,13); TextOnPage(0,w.at,8,27,134,12,8);
                PictureOnPage(0,w.photo,8,42,134,82);
                if(w.measured!=null) TextOnPage(0,"Measured  "+w.measured,8,128,134,15,8);
                if(w.dimensions!=null) TextOnPage(0,"Made  "+w.dimensions,8,144,134,14,8);
                // F77 (the user): a finished work has Export (the web code) and Edit; Edit brings the work out first when it
                // is not in this room (it was a separate Place). An unfinished work has no file buttons (Print / Send to PC
                // are gone): finish it, then export.
                if (w.completedOn != null)
                {
                    if (!string.IsNullOrEmpty(w.pickupCode)) TextOnPage(0, w.pickupCode + "  " + w.pickupUrl, 8, 154, 134, 12, 7);
                    BookButton(0,ExportLabel,8,168,88,()=>GetCreation(w));
                    BookButton(0,"Edit",100,168,42,()=>EditWork(w));
                }
                // Fixed slots: missing data is blank paper, never another image or a placeholder.
                TextOnPage(1,"Reference",8,6,134,14,11);
                PictureOnPage(1,w.reference,8,23,61,30);PictureOnPage(1,w.look,78,23,61,30);
                TextOnPage(1,"Your sketch",8,57,134,14,11);
                PictureOnPage(1,w.sketch,8,72,65,24);
                TextOnPage(1,"Colours / Textures",8,100,134,14,11);
                if(w.swatches.Count>0) {
                    // Every swatch is included; scale into up to two rows rather than showing the global palette.
                    int columns=Mathf.CeilToInt(Mathf.Sqrt(w.swatches.Count * 5f)); columns=Mathf.Min(columns,w.swatches.Count); float cell=130f/columns;
                    int rows=Mathf.CeilToInt((float)w.swatches.Count/columns); float rowH=18f/rows;
                    for(int i=0;i<w.swatches.Count;i++) {
                        var sw=w.swatches[i]; float x=8+(i%columns)*cell;
                        float sy=117+(i/columns)*rowH;
                        if(sw.image!=null) PictureOnPage(1,sw.image,x,sy,Mathf.Min(20,cell-2),rowH-1);
                        else SwatchOnPage(1,sw.hex,x,sy,Mathf.Min(cell-2,rowH-1));
                    }
                }
                TextOnPage(1,"Shape",8,137,134,12,11);
                if (w.completedOn != null && JourneyImages(w).Any()) BookButton(0,"Journey",91,6,51,()=>ShowNotebookJourney(w,0));
                if(!string.IsNullOrWhiteSpace(w.shape)) {
                    // F87 (the user: "what I wrote about the shape was cut off"): the whole words, smaller when long.
                    TextOnPage(1,w.shape,8,150,134,21,9,4.5f);
                }
            }
            BookButton(1,"All makes",8,172,54,()=>{NotebookSpread=0;FillSpread();});
            BookButton(1,"<",65,172,22,()=>FlipNotebook(-1),NotebookSpread>0);
            BookButton(1,">",90,172,22,()=>FlipNotebook(1),NotebookSpread<NotebookSpreads-1);
            BookButton(1,"x",117,172,25,CloseNotebook);
        }

        private RectTransform NotebookCanvas(int page) => (RectTransform)pageTitles[page].transform.parent;
        private void TextOnPage(int page,string text,float x,float y,float w,float h,float size,float minSize=-1f)
        {
            var label=HatIdeaCard.Label(NotebookCanvas(page),font,x,y,w,h,minSize>0f?minSize:size*.8f,size,notebookInk,FontStyles.Normal,TextAlignmentOptions.TopLeft);
            label.text=text ?? ""; label.raycastTarget=false; notebookWidgets.Add(label.gameObject);
        }
        private void PictureOnPage(int page,string path,float x,float y,float w,float h)
        {
            if(path==null) return; var texture=LoadNoteImage(path); if(texture==null) return;
            var go=new GameObject("Notebook Picture",typeof(RectTransform),typeof(RawImage));
            var r=(RectTransform)go.transform; r.SetParent(NotebookCanvas(page),false);
            r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1); r.anchoredPosition=new Vector2(x,-y); r.sizeDelta=new Vector2(w,h);
            var image=go.GetComponent<RawImage>(); image.texture=texture; image.raycastTarget=false;
            float aspect=(float)texture.width/texture.height; r.sizeDelta=aspect>w/h?new Vector2(w,w/aspect):new Vector2(h*aspect,h);
            notebookWidgets.Add(go);
        }
        private void SwatchOnPage(int page,string hex,float x,float y,float size)
        {
            if(!ColorUtility.TryParseHtmlString(hex,out var tint))return;
            var texture=new Texture2D(32,32,TextureFormat.RGBA32,false);
            for(int py=0;py<32;py++) for(int px=0;px<32;px++) {
                float dx=(px-15.5f)/15.5f,dy=(py-15.5f)/15.5f;
                float radius=.86f+.08f*Mathf.Sin(Mathf.Atan2(dy,dx)*5);
                texture.SetPixel(px,py,dx*dx+dy*dy<radius*radius?tint:Color.clear);
            }
            texture.Apply();noteTextures.Add(texture);
            var go=new GameObject("Paint dab",typeof(RectTransform),typeof(RawImage));
            var r=(RectTransform)go.transform;r.SetParent(NotebookCanvas(page),false);
            r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=new Vector2(x,-y);r.sizeDelta=new Vector2(size,size);
            go.GetComponent<RawImage>().texture=texture;go.GetComponent<RawImage>().raycastTarget=false;notebookWidgets.Add(go);
        }
        private void AddNotebookHit(int page,float x,float y,float w,float h,Action click)
        {
            notebookButtons.Add((NotebookRect(page,x,y,w,h),click));
        }
        private void ExportNotebook(NotebookWork work,bool send)
        {
            // F57: the made model on the desk is printed as a closed solid (STL + 3MF + GLB), not copied from its view
            if (send && Product != null && Design?.kind == "model" && project != null && work.id == project) { Print(); return; }
            if(work.stl==null || !File.Exists(work.stl)) { if (Product != null && Design?.kind == "model" && work.id == project) Print(); return; }
            try {
                string folder=Path.Combine(Application.persistentDataPath,"prints"); Directory.CreateDirectory(folder);
                LastPrint=Path.Combine(folder,Path.GetFileName(work.stl)); File.Copy(work.stl,LastPrint,true);
                byte[] colour=work.colourFile!=null && File.Exists(work.colourFile)?File.ReadAllBytes(work.colourFile):null;
                if(colour!=null) { LastColourPrint=Path.ChangeExtension(LastPrint,".3mf"); File.WriteAllBytes(LastColourPrint,colour); }
                if(send) StartCoroutine(Send(File.ReadAllBytes(work.stl),Path.GetFileName(work.stl),colour,Path.ChangeExtension(Path.GetFileName(work.stl),".3mf")));
                else Say(Crew.Maker,"Print file saved. Send it to your PC when you're ready.");
            } catch(Exception e) { Say(Crew.Maker,"I couldn't save that file. Please try again."); Debug.LogWarning(e.Message); }
        }
    }
}
