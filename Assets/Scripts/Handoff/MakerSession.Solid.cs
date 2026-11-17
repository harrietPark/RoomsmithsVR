using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // F57 (2026-11-13, the user: "can we take what we made to a real 3D printer or into a 3D program?"): a made
    // model leaves the headset as a real solid. "Print it" rebuilds it (SolidRebuild: closed, one piece, F59's cuts
    // and sculpting in) off the main thread and writes three files beside each other in `prints/`, all sent to the
    // PC's WebLab like the STL was:
    //  - name.stl: the closed solid, millimetres, z up (any slicer);
    //  - name.3mf: the same with at most 4 colours (its texture reduced, painted parts as painted);
    //  - name.glb: what is seen in the room, real size, with its textures (Blender, 3D Viewer, the web).
    // Coasters, vessels, trays and clips are built closed by the app's own code (VerifyMake3D, VerifyRoomFit): they
    // keep their exact print path and only gain the GLB.
    // A made model with F59 operations (hollow, holes, notches, sculpting) is shown rebuilt too: its view is a
    // coarser solid of the same field with the texture's colours on its vertices.
    public sealed partial class MakerSession
    {
        public string LastGlb { get; private set; }
        public SolidRebuild.Result LastPrintSolid { get; private set; }
        public SolidRebuild.Stats LastSolidStats { get; private set; }
        public bool Printing { get; private set; }
        public int SolidViews { get; private set; }       // made things shown rebuilt (tests)
        public string LastSolidError { get; private set; }

        private Material solidLook;

        // Main-thread cost of each F57/F59 section (worst ms since Play started), for the checks (FPS: never under 60).
        private static readonly Dictionary<string, float> worstMs = new Dictionary<string, float>();
        public static string SolidHitchReport => string.Join(", ", worstMs.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value.ToString("0.0") + " ms"));
        public static float SolidWorstMs => worstMs.Count > 0 ? worstMs.Values.Max() : 0f;
        private static long HitchStart() => System.Diagnostics.Stopwatch.GetTimestamp();
        private static void HitchEnd(string section, long start)
        {
            float ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000f / System.Diagnostics.Stopwatch.Frequency;
            if (!worstMs.TryGetValue(section, out var worst) || ms > worst) worstMs[section] = ms;
        }
        private static int readbackRows; // 0 unknown, 1 bottom row first, 2 top row first, -1 no readback
        private readonly Dictionary<string, (Color32[] pixels, int size)> texturePixels = new Dictionary<string, (Color32[], int)>();
        private SolidTicker solidTicker;

        // ------------------------------------------------------------ the source of a design

        // The design's own mesh (a model at its made size, welded, with its texture layout), without F59 operations.
        private static bool SourceArrays(CoasterDesign d, out Vector3[] v, out int[] t, out Vector2[] uv)
        {
            v = null; t = null; uv = null;
            if (d == null) return false;
            var clean = d.Copy();
            clean.ops = null;
            var mesh = CoasterMesh.Build(clean);
            if (mesh == null) return false;
            v = mesh.vertices; t = mesh.triangles; uv = mesh.uv;
            Destroy(mesh);
            return v.Length > 0 && t.Length > 2;
        }

        // The model's texture as pixels (bottom row first), read back from the GPU at `size` (no main-thread decode).
        private IEnumerator ReadTexture(CoasterDesign d, int size = 512)
        {
            if (d == null || string.IsNullOrEmpty(d.texture) || texturePixels.ContainsKey(d.texture)) yield break;
            var m = MadeMaterial(d);
            if (m == null || m == material || !m.HasProperty("_BaseMap")) yield break;
            var tex = m.GetTexture("_BaseMap");
            if (tex == null || !SystemInfo.supportsAsyncGPUReadback) yield break;
            if (readbackRows == 0) yield return CheckRows();
            if (readbackRows < 0) yield break;
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            float until = Time.realtimeSinceStartup + 3f;
            while (!request.done && Time.realtimeSinceStartup < until) yield return null;
            RenderTexture.ReleaseTemporary(rt);
            if (!request.done || request.hasError) yield break;
            var data = request.GetData<Color32>();
            if (data.Length != size * size) yield break;
            var pixels = new Color32[size * size];
            data.CopyTo(pixels);
            bool flip = readbackRows == 2;
            if (flip) pixels = FlipRows(pixels, size);
            texturePixels[d.texture] = (pixels, size);
        }

        private static Color32[] FlipRows(Color32[] p, int size)
        {
            var o = new Color32[p.Length];
            for (int y = 0; y < size; y++) Array.Copy(p, y * size, o, (size - 1 - y) * size, size);
            return o;
        }

        private IEnumerator CheckRows()
        {
            var probe = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            probe.SetPixels32(new[] { new Color32(255, 0, 0, 255), new Color32(255, 0, 0, 255), new Color32(0, 0, 255, 255), new Color32(0, 0, 255, 255) }); // bottom red, top blue
            probe.Apply();
            var rt = RenderTexture.GetTemporary(2, 2, 0);
            Graphics.Blit(probe, rt);
            var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            float until = Time.realtimeSinceStartup + 2f;
            while (!request.done && Time.realtimeSinceStartup < until) yield return null;
            readbackRows = -1;
            if (request.done && !request.hasError)
            {
                var data = request.GetData<Color32>();
                if (data.Length == 4) readbackRows = data[0].r > data[0].b ? 1 : 2;
            }
            RenderTexture.ReleaseTemporary(rt);
            Destroy(probe);
        }

        // A rebuild job for a made thing (the arrays are copied here; the work runs in Task.Run).
        private SolidRebuild.Job SolidJob(CoasterDesign d, bool forPrint, IEnumerable<SolidRebuild.Op> extra = null)
        {
            if (!SourceArrays(d, out var v, out var t, out var uv)) return null;
            var job = new SolidRebuild.Job { vertices = v, triangles = t, uv = uv, forPrint = forPrint, colour = d.Tint(new Color(.95f, .85f, .62f)) };
            if (!string.IsNullOrEmpty(d.texture) && texturePixels.TryGetValue(d.texture, out var tex)) { job.texture = tex.pixels; job.textureWidth = job.textureHeight = tex.size; }
            if (d.ops != null) job.ops.AddRange(d.ops.Where(o => o != null).Select(o => o.Copy()));
            if (extra != null) job.ops.AddRange(extra);
            return job;
        }

        private static IEnumerator RunJob(SolidRebuild.Job job)
        {
            var task = Task.Run(() => SolidRebuild.Run(job));
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) job.error = task.Exception?.GetBaseException().Message ?? "failed";
        }

        // ------------------------------------------------------------ print it

        // MakerSession.Print hands a model here (the other kinds keep their own exact print path).
        private IEnumerator PrintSolid()
        {
            if (Product == null || Design == null) yield break;
            Printing = true;
            var design = Design.Copy();
            var thing = Product;
            SayPrintCheck(true);
            Caption(Crew.Fixer, "Making it print-ready…", 4f);
            yield return ReadTexture(design);
            long h = HitchStart();
            var job = SolidJob(design, true);
            HitchEnd("print: job", h);
            if (job == null) { Printing = false; Say(Crew.Fixer, "I couldn't read the shape to print."); yield break; }
            yield return RunJob(job);
            if (job.error != null || job.result == null)
            {
                Printing = false;
                LastSolidError = job.error;
                Debug.LogWarning("[Windups Solid] print: " + job.error);
                Say(Crew.Fixer, "The print file didn't work out. Try again?");
                yield break;
            }
            LastPrintSolid = job.result;
            LastSolidStats = job.result.stats;
            // painted parts as painted, the rest in its texture's colours (at most 4)
            h = HitchStart();
            var paint = thing != null ? thing.GetComponentInChildren<PaintableThing>() : null;
            var paintedJob = PaintedJob(paint, design); // (F90b: the per-triangle work happens in the task below)
            var glbInput = GlbInput(thing, design);
            HitchEnd("print: inputs", h);
            string stem = Regex.Replace(design.name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-') + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string folder = Path.Combine(Application.persistentDataPath, "prints");
            var summary = JsonConvert.SerializeObject(new { Need, Style, look = Look?.label, Shape, design, material = Material, minutes = PrintMinutes,
                solid = new { triangles = job.result.triangles.Length / 3, closed = job.result.Closed, nonManifold = job.result.nonManifoldEdges, volumeCm3 = job.result.volumeCm3,
                              holesClosed = job.result.stats.sourceOpenEdges, cellMm = job.result.stats.cellMm } }, Formatting.Indented);
            string name = design.name;
            var write = Task.Run(() =>
            {
                var r = job.result;
                var painted = PaintedColours(paintedJob, r);
                var stl = PrintFiles.Stl(r.vertices, r.triangles, 1f, "Roomsmiths " + name);
                var palette = PrintFiles.Reduce(r.colours, r.triangles, painted, PrintFiles.MaxPrintColours, out var index);
                var threeMf = PrintFiles.ThreeMf(r.vertices, r.triangles, index, palette, null, 1f, name);
                var glb = glbInput != null ? PrintFiles.Glb(name, glbInput.v, glbInput.n, glbInput.uv0, glbInput.UV1, glbInput.colours, glbInput.parts) : null;
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, stem + ".stl"), stl);
                File.WriteAllBytes(Path.Combine(folder, stem + ".3mf"), threeMf);
                if (glb != null) File.WriteAllBytes(Path.Combine(folder, stem + ".glb"), glb);
                File.WriteAllText(Path.Combine(folder, stem + ".json"), summary);
                return (stl, threeMf, glb, palette.Count);
            });
            while (!write.IsCompleted) yield return null;
            Printing = false;
            if (write.IsFaulted) { LastError = write.Exception?.GetBaseException().Message; Say(Crew.Fixer, "I couldn't save the print files."); yield break; }
            var (stlBytes, mfBytes, glbBytes, colours) = write.Result;
            LastPrint = Path.Combine(folder, stem + ".stl");
            LastColourPrint = Path.Combine(folder, stem + ".3mf");
            LastGlb = glbBytes != null ? Path.Combine(folder, stem + ".glb") : null;
            AttachPrintToNotebook(thing, LastPrint, LastColourPrint);
            Talk.Add("Person printed it: closed solid of " + (job.result.triangles.Length / 3) + " triangles, " + colours + " colours");
            if (thing != null) ToyFx.Confetti(thing.position + Vector3.up * .05f, 16);
            yield return SendFiles(new List<(byte[], string, string)>
            {
                (stlBytes, stem + ".stl", "model/stl"), (mfBytes, stem + ".3mf", "model/3mf"), (glbBytes, stem + ".glb", "model/gltf-binary")
            });
        }

        private IEnumerator SendFiles(List<(byte[] data, string name, string type)> files)
        {
            LastPrintSent = false;
            string pc = WindupsDirectCredentials.PcUrl;
            if (!string.IsNullOrEmpty(pc))
                foreach (var (data, name, type) in files)
                {
                    if (data == null) continue;
                    using (var http = new UnityWebRequest(pc + "/api/print?name=" + Uri.EscapeDataString(name), "POST"))
                    {
                        http.uploadHandler = new UploadHandlerRaw(data);
                        http.downloadHandler = new DownloadHandlerBuffer();
                        http.SetRequestHeader("Content-Type", type);
                        http.timeout = 20;
                        yield return http.SendWebRequest();
                        bool ok = http.result == UnityWebRequest.Result.Success;
                        if (name.EndsWith(".stl", StringComparison.Ordinal)) LastPrintSent = ok;
                        if (!ok) break;
                    }
                }
            Say(Crew.Maker, LastPrintSent ? "Sent to your PC: print files and a 3D file, in the prints folder." : "Saved on the headset. Your PC wasn't reachable.");
        }

        // The colour of each solid triangle that lies on a painted part (by the part's own rule), else none.
        private static Color32?[] PaintedColours(PaintableThing paint, CoasterDesign d, SolidRebuild.Result r) => PaintedColours(PaintedJob(paint, d), r);

        // What PaintedColours needs from Unity objects, read on the main thread (the rest is plain arithmetic).
        private sealed class Painted { public Dictionary<string, Color32> byName; public Func<Vector3, Vector3, Bounds, int> classify; public string[] names; }
        private static Painted PaintedJob(PaintableThing paint, CoasterDesign d)
        {
            if (paint == null || paint.Marks.Count == 0 || paint.Parts == null) return null;
            var byName = new Dictionary<string, Color32>();
            foreach (var mark in paint.Marks.Values)
                if (mark != null && ColorUtility.TryParseHtmlString(mark.hex ?? "", out var c)) byName[mark.part] = c;
            if (byName.Count == 0) return null;
            return new Painted { byName = byName, classify = FitShapes.ClassifierOf(d), names = FitShapes.PartsOf(d) };
        }

        private static Color32?[] PaintedColours(Painted job, SolidRebuild.Result r)
        {
            if (job == null || r == null || r.vertices.Length == 0) return null;
            var byName = job.byName;
            var classify = job.classify;
            var names = job.names;
            var bounds = new Bounds(r.vertices[0], Vector3.zero);
            foreach (var p in r.vertices) bounds.Encapsulate(p);
            var result = new Color32?[r.triangles.Length / 3];
            for (int i = 0; i + 2 < r.triangles.Length; i += 3)
            {
                Vector3 a = r.vertices[r.triangles[i]], b = r.vertices[r.triangles[i + 1]], c = r.vertices[r.triangles[i + 2]];
                Vector3 face = Vector3.Cross(b - a, c - a);
                float m = face.magnitude;
                if (m < 1e-14f) continue;
                int part = Mathf.Clamp(classify(face / m, (a + b + c) / 3f, bounds), 0, names.Length - 1);
                if (byName.TryGetValue(names[part], out var colour)) result[i / 3] = colour;
            }
            return result;
        }

        // ------------------------------------------------------------ the GLB of what is seen

        private sealed class GlbData
        {
            public Vector3[] v, n;
            public Vector2[] uv0;
            // 2026-11-15 (F90b): the box layout is worked out where it is first read (the file-writing tasks), not on
            // the main thread: a solid lamp's shown mesh has ~100k vertices.
            private Vector2[] uv1;
            public Vector2[] UV1 => uv1 ??= PrintFiles.BoxUv(v, n, PaintableThing.TileSize);
            public Color32[] colours;
            public List<PrintFiles.GlbPart> parts = new List<PrintFiles.GlbPart>();
        }

        // The made thing's parts as they look now: the model's own texture, a painted colour or a painted material tile.
        private GlbData GlbInput(Transform thing, CoasterDesign d)
        {
            var paint = thing != null ? thing.GetComponentInChildren<PaintableThing>() : null;
            if (paint == null || paint.Mesh == null) return null;
            var mesh = paint.Mesh;
            var g = new GlbData { v = mesh.vertices, n = mesh.normals };
            var uv = mesh.uv;
            // (a rebuilt solid has no texture layout of its own: its colours are on its vertices)
            bool rebuilt = d?.ops != null && d.ops.Length > 0;
            bool ownTexture = !rebuilt && d != null && d.kind == "model" && !string.IsNullOrEmpty(d.texture) && File.Exists(d.texture) && uv != null && uv.Length == g.v.Length;
            if (ownTexture) g.uv0 = uv;
            var colors = mesh.colors32;
            if (colors != null && colors.Length == g.v.Length && !ownTexture) g.colours = colors;
            byte[] own = ownTexture ? File.ReadAllBytes(d.texture) : null;
            var baseColour = d != null ? d.Tint(new Color(.95f, .85f, .62f)) : Color.white;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var part = new PrintFiles.GlbPart { name = paint.Parts != null && s < paint.Parts.Length ? paint.Parts[s] : "part " + s, triangles = mesh.GetTriangles(s) };
                if (paint.Marks.TryGetValue(s, out var mark) && mark != null)
                {
                    part.colour = ColorUtility.TryParseHtmlString(mark.hex ?? "", out var c) ? c : Color.white;
                    if (mark.kind == "texture" && !string.IsNullOrEmpty(mark.image) && File.Exists(mark.image))
                    { part.image = File.ReadAllBytes(mark.image); part.mime = "image/jpeg"; part.texCoord = 1; part.colour = Color.white; }
                    part.roughness = mark.kind == "texture" ? .75f : .45f;
                }
                else if (own != null) { part.image = own; part.mime = "image/png"; part.texCoord = 0; part.colour = Color.white; }
                else part.colour = g.colours != null ? Color.white : baseColour;
                g.parts.Add(part);
            }
            return g;
        }

        // Coasters, trays and clips (their own print path): the GLB beside their STL, sent after it.
        private IEnumerator GlbBeside(string stlPath)
        {
            if (string.IsNullOrEmpty(stlPath) || Product == null) yield break;
            var input = GlbInput(Product, Design);
            if (input == null) yield break;
            string name = Design?.name ?? "Made";
            string path = Path.ChangeExtension(stlPath, ".glb");
            var write = Task.Run(() => { var glb = PrintFiles.Glb(name, input.v, input.n, input.uv0, input.UV1, input.colours, input.parts); File.WriteAllBytes(path, glb); return glb; });
            while (!write.IsCompleted) yield return null;
            if (write.IsFaulted) yield break;
            LastGlb = path;
            string pc = WindupsDirectCredentials.PcUrl;
            yield return new WaitForSeconds(1f); // after the STL and 3MF
            if (string.IsNullOrEmpty(pc)) yield break;
            using (var http = new UnityWebRequest(pc + "/api/print?name=" + Uri.EscapeDataString(Path.GetFileName(path)), "POST"))
            {
                http.uploadHandler = new UploadHandlerRaw(write.Result);
                http.downloadHandler = new DownloadHandlerBuffer();
                http.SetRequestHeader("Content-Type", "model/gltf-binary");
                http.timeout = 20;
                yield return http.SendWebRequest();
            }
        }

        // ------------------------------------------------------------ made things with F59 operations

        // From AfterSolid: a model with operations is shown rebuilt (in the background; the plain model shows meanwhile).
        private void SolidAfterMade(Transform made, CoasterDesign d)
        {
            EnsureSolidTicker();
            if (made == null || d == null || d.kind != "model" || d.ops == null || d.ops.Length == 0) return;
            StartCoroutine(ShowRebuilt(made, d.Copy(), null));
        }

        private void EnsureSolidTicker()
        {
            if (solidTicker != null) return;
            solidTicker = gameObject.GetComponent<SolidTicker>() ?? gameObject.AddComponent<SolidTicker>();
            solidTicker.maker = this;
        }

        // Rebuilds the view of a made thing from its design's operations and swaps its mesh; `done` gets the job.
        private IEnumerator ShowRebuilt(Transform made, CoasterDesign d, Action<SolidRebuild.Job> done)
        {
            yield return ReadTexture(d);
            var job = SolidJob(d, false);
            if (job == null) { done?.Invoke(null); yield break; }
            yield return RunJob(job);
            if (made == null || job.result == null || job.error != null)
            {
                LastSolidError = job.error;
                if (job.error != null) Debug.LogWarning("[Windups Solid] view: " + job.error);
                done?.Invoke(job);
                yield break;
            }
            yield return SwapView(made, d, job.result, false);
            done?.Invoke(job);
        }

        // Puts a rebuilt solid on a made thing as its view and its paintable parts (split off the main thread).
        private IEnumerator SwapView(Transform made, CoasterDesign d, SolidRebuild.Result r, bool newCollider)
        {
            long h0 = HitchStart();
            var paint = made != null ? made.GetComponentInChildren<PaintableThing>() : null;
            if (paint == null) yield break;
            var filter = paint.GetComponent<MeshFilter>();
            var renderer = paint.GetComponent<MeshRenderer>();
            var marks = paint.SavedMarks();
            var classify = FitShapes.ClassifierOf(d);
            var names = FitShapes.PartsOf(d);
            float metres = Mathf.Abs(filter.transform.lossyScale.x) / PaintableThing.TileSize;
            string name = "Solid " + d.name;
            HitchEnd("view prepare", h0);
            // (colours and bounds of tens of thousands of vertices: in the task too)
            var prepare = Task.Run(() =>
            {
                var colours = new Color[r.vertices.Length];
                if (r.colours != null) for (int i = 0; i < colours.Length; i++) colours[i] = r.colours[i];
                else for (int i = 0; i < colours.Length; i++) colours[i] = Color.white;
                var bounds = new Bounds(r.vertices[0], Vector3.zero);
                foreach (var p in r.vertices) bounds.Encapsulate(p);
                return PaintableThing.Prepare(r.vertices, r.normals, colours, null, r.triangles, bounds, metres, classify, names, name);
            });
            while (!prepare.IsCompleted) yield return null;
            if (prepare.IsFaulted || made == null || filter == null) yield break;
            long h = HitchStart();
            renderer.sharedMaterial = SolidLook(d);
            var thing = PaintableThing.MakePrepared(filter, renderer, prepare.Result, false);
            ApplyMarks(made, marks);
            if (made == Product) ProductMesh = thing.Mesh;
            HitchEnd("view swap", h);
            SolidViews++;
            if (newCollider) yield return BakeCollider(made, thing.Mesh);
            SolidLookChanged(made, d);
        }

        // A convex collider for a changed shape, cooked off the main thread (Physics.BakeMesh is thread safe).
        private IEnumerator BakeCollider(Transform made, Mesh mesh)
        {
            var collider = made != null ? made.GetComponentInChildren<MeshCollider>() : null;
            if (collider == null || mesh == null) yield break;
            var id = mesh.GetEntityId();
            var bake = Task.Run(() => Physics.BakeMesh(id, true));
            while (!bake.IsCompleted) yield return null;
            long h = HitchStart();
            if (collider != null && mesh != null) collider.sharedMesh = mesh;
            HitchEnd("collider", h);
        }

        // Rebuilt solids carry their colours on their vertices (Windups/True Colour with vertex colours).
        private Material SolidLook(CoasterDesign d)
        {
            if (solidLook != null) return solidLook;
            var template = TrueTemplate;
            if (template == null) return material;
            solidLook = new Material(template) { name = "Solid look" };
            solidLook.SetTexture("_BaseMap", Texture2D.whiteTexture);
            solidLook.SetColor("_BaseColor", Color.white);
            if (solidLook.HasProperty("_VertexColour")) solidLook.SetFloat("_VertexColour", 1f);
            if (solidLook.HasProperty("_Gloss")) solidLook.SetFloat("_Gloss", .12f);
            owned.Add(solidLook);
            return solidLook;
        }

        // ------------------------------------------------------------ every frame (F59 ghosts, F59 sculpting)

        private void SolidFrame()
        {
            FeaturesFrame();
            SculptFrame();
            TagClearFrame();     // 2026-11-13: name tags give way to cards, pills and pictures
            ColourWheelFrame();  // 2026-11-13: the palette's paint tube and its colour wheel
            PartsFrame();        // F83: the model's part under a fingertip lights up and shows its name
        }

        // Ticks the session's F57/F59 frame steps (added to the session's object; keeps them out of the shared step list).
        public sealed class SolidTicker : MonoBehaviour
        {
            public MakerSession maker;
            private int errors;
            private void Update()
            {
                if (maker == null || !maker.isActiveAndEnabled) return;
                maker.FrameLogTick(); // 2026-11-15: frame times into maker_input_log.txt (MakerSession.FrameLog.cs)
                try { maker.SolidFrame(); }
                catch (Exception exception) { if (errors++ < 3) Debug.LogError("[Windups Solid] frame: " + exception); }
            }
        }

        // ------------------------------------------------------------ tests

        public void SimulatePrintSolid() { if (Product != null && Design != null) StartCoroutine(PrintSolid()); }
    }
}
