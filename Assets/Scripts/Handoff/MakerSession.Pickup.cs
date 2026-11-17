using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Windups.Handoff
{
    public sealed partial class MakerSession
    {
        private bool collectingCreation;
        private string pickupFailure;
        private const string DeviceTokenKey = "roomsmiths.pickup.device";

        private static string PickupOrigin
        {
            get
            {
#if UNITY_EDITOR
                return Environment.GetEnvironmentVariable("ROOMSMITHS_COLLECTION_ORIGIN") ?? "http://localhost:5177";
#else
                return Resources.Load<TextAsset>("RoomsmithsCollectionOrigin")?.text.Trim();
#endif
            }
        }

        private static string PickupDeviceToken()
        {
            var saved = PlayerPrefs.GetString(DeviceTokenKey, "");
            if (saved.Length == 64) return saved;
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            saved = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            PlayerPrefs.SetString(DeviceTokenKey, saved);
            PlayerPrefs.Save();
            return saved;
        }

        // F77 (the user: "call it Export; if it's already on the web say so, else give the code and the site; edited since,
        // a new export"): the code is cleared whenever the work is edited (ReopenWork) or finished again (StartEnding),
        // so a code still kept means the web has this very version.
        public const string ExportLabel = "Export";
        public int ExportsRepeated { get; private set; }
        private void GetCreation(NotebookWork work)
        {
            if (work == null || collectingCreation) return;
            LoadNotes();
            if ((work.finalNameReady || Scripted) && !string.IsNullOrEmpty(work.pickupCode) && !string.IsNullOrEmpty(work.pickupUrl))
            {
                ExportsRepeated++;
                CloseQuestion("already exported");
                string site = Uri.TryCreate(work.pickupUrl, UriKind.Absolute, out var known) ? known.Authority : work.pickupUrl;
                Caption(Crew.Fixer, "It's already on the web.\nGo to " + site + "\nCode: " + work.pickupCode, 30f);
                nextLineAt = Mathf.Max(nextLineAt, Time.time + 30f);
                WriteInputLog("export: already on the web (" + work.pickupCode + ")", true);
                return;
            }
            var thing = MadeThings().Where(t => notebookOwners.TryGetValue(t, out var id) && id == work.id)
                .OrderByDescending(t => t == Product).ThenByDescending(t => StandUnder(t) != null).FirstOrDefault();
            if (thing == null || !madeDesigns.TryGetValue(thing, out var design))
            {
                Caption(Crew.Fixer, "Bring the finished piece into this room, then try again.", 5f);
                return;
            }
            CloseQuestion("web pickup selected");
            StartCoroutine(PreparePickup(work, thing, design.Copy()));
        }

        private IEnumerator PreparePickup(NotebookWork work, Transform thing, CoasterDesign design)
        {
            collectingCreation = true;
            pickupFailure = null;
            ExportLeftOut = "";
            BeginExportWait(thing); // F87: Fixer looks at the work and thinks; a paper plane flies off per file sent
            try
            {
                yield return NameFinishedWork(work, thing, design);
                yield return WritePickupFiles(work, thing, design);
                string origin = PickupOrigin?.TrimEnd('/');
                if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "https" && !(Application.isEditor && uri.IsLoopback && uri.Scheme == "http")))
                {
                    Say(Crew.Fixer, "Your files are saved here. The download site isn't connected yet.");
                    Say(Crew.Maker, "Peel a fresh note whenever you want to make something new.");
                    yield break;
                }
                var paths = new Dictionary<string, string> { ["stl"] = work.stl, ["3mf"] = work.colourFile, ["glb"] = work.glb, ["fbx"] = work.fbx, ["obj"] = work.obj, ["jpg"] = work.photo };
                if (uri.Scheme == "https")
                {
                    PickupReceipt cloudReceipt = null;
                    yield return UploadCloudPickup(origin, work, design, paths, receipt => cloudReceipt = receipt);
                    FinishPickup(work, origin, cloudReceipt);
                    yield break;
                }
                var pack = Task.Run(() =>
                {
                    var files = paths.Where(entry => !string.IsNullOrEmpty(entry.Value) && File.Exists(entry.Value))
                        .ToDictionary(entry => entry.Key, entry => Convert.ToBase64String(File.ReadAllBytes(entry.Value)));
                    if (!files.ContainsKey("stl")) return null;
                    return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new {
                        id = work.id, name = work.name ?? design.name, description = work.dimensions ?? work.topic,
                        createdOn = work.completedOn, files
                    }));
                });
                while (!pack.IsCompleted) yield return null;
                if (pack.IsFaulted || pack.Result == null)
                {
                    Say(Crew.Fixer, "The model files aren't ready. You can try again in the notebook.");
                    yield break;
                }
                using var request = new UnityWebRequest(origin + "/api/collect/works", "POST");
                request.uploadHandler = new UploadHandlerRaw(pack.Result);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + PickupDeviceToken());
                request.timeout = 60;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Say(Crew.Fixer, "Still saved here. Try again from your notebook when you're online.");
                    yield break;
                }
                ExportSent(1, 1); // F87
                FinishPickup(work, origin, JsonConvert.DeserializeObject<PickupReceipt>(request.downloadHandler.text));
            }
            finally
            {
                collectingCreation = false;
                EndExportWait(!string.IsNullOrEmpty(work.pickupCode));
            }
        }

        private IEnumerator UploadCloudPickup(string origin, NotebookWork work, CoasterDesign design,
            Dictionary<string, string> paths, Action<PickupReceipt> finished)
        {
            var sizes = paths.Where(pair => !string.IsNullOrEmpty(pair.Value) && File.Exists(pair.Value))
                .ToDictionary(pair => pair.Key, pair => new FileInfo(pair.Value).Length);
            if (!sizes.ContainsKey("stl"))
            {
                pickupFailure = "The print file couldn't be prepared. Try Export again.";
                yield break;
            }
            // F87 (Quest: "your FBX exceeds the web size limit... it's saved here"): the print files go first; a viewing
            // file (GLB, FBX, OBJ) that does not fit the limits is left out (it stays on the headset) instead of
            // stopping the whole export.
            const long mib = 1024 * 1024;
            var all = sizes;
            sizes = new Dictionary<string, long>();
            var left = new List<string>();
            long total = 0;
            foreach (var key in new[] { "stl", "3mf", "jpg", "glb", "fbx", "obj" })
            {
                if (!all.TryGetValue(key, out long length)) continue;
                if (length > (key == "jpg" ? mib : 20 * mib) || total + length > 32 * mib) { left.Add(key); continue; }
                sizes[key] = length;
                total += length;
            }
            if (left.Count > 0) WriteInputLog("export: left out (too big) " + string.Join(", ", left) + " | " + JsonConvert.SerializeObject(all), true);
            ExportLeftOut = string.Join(", ", left);
            if (!sizes.ContainsKey("stl"))
            {
                pickupFailure = "The print file is too big for the web. It's saved on this headset.";
                yield break;
            }
            int sent = 0;
            var metadata = JsonConvert.SerializeObject(new {
                id = work.id, name = work.name ?? design.name, description = work.dimensions ?? work.topic,
                createdOn = work.completedOn, files = sizes
            });
            string route = origin + "/api/collect/uploads";
            string token = "Bearer " + PickupDeviceToken();
            string revision;
            using (var begin = new UnityWebRequest(route, "POST"))
            {
                begin.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(metadata));
                begin.downloadHandler = new DownloadHandlerBuffer();
                begin.SetRequestHeader("Authorization", token);
                begin.SetRequestHeader("Content-Type", "application/json");
                begin.timeout = 25;
                yield return begin.SendWebRequest();
                if (begin.result != UnityWebRequest.Result.Success) { PickupRequestFailed(begin, "start"); yield break; }
                revision = JsonConvert.DeserializeObject<PickupReceipt>(begin.downloadHandler.text)?.revision;
                if (!Guid.TryParse(revision, out _)) yield break;
            }
            foreach (var entry in sizes)
            {
                using var upload = new UnityWebRequest(route + "/" + revision + "/" + entry.Key, "PUT");
                upload.uploadHandler = new UploadHandlerFile(paths[entry.Key]);
                upload.downloadHandler = new DownloadHandlerBuffer();
                upload.SetRequestHeader("Authorization", token);
                upload.SetRequestHeader("Content-Type", "application/octet-stream");
                upload.timeout = 60;
                yield return upload.SendWebRequest();
                if (upload.result != UnityWebRequest.Result.Success) { PickupRequestFailed(upload, entry.Key); yield break; }
                ExportSent(++sent, sizes.Count);
            }
            using var commit = new UnityWebRequest(route + "/" + revision + "/commit", "POST");
            commit.downloadHandler = new DownloadHandlerBuffer();
            commit.SetRequestHeader("Authorization", token);
            commit.timeout = 25;
            yield return commit.SendWebRequest();
            if (commit.result == UnityWebRequest.Result.Success)
                finished(JsonConvert.DeserializeObject<PickupReceipt>(commit.downloadHandler.text));
            else PickupRequestFailed(commit, "finish");
        }

        private void PickupRequestFailed(UnityWebRequest request, string stage)
        {
            WriteInputLog("export " + stage + " failed: HTTP " + request.responseCode + " " + request.error, true);
            pickupFailure = request.responseCode == 0
                ? "Couldn't reach the download site. Your files are saved here. Try Export again when connected."
                : "The download site couldn't accept your files (HTTP " + request.responseCode + "). Your files are saved here.";
        }

        private void FinishPickup(NotebookWork work, string origin, PickupReceipt receipt)
        {
            if (receipt == null || string.IsNullOrWhiteSpace(receipt.code))
            {
                Caption(Crew.Fixer, pickupFailure ?? "The download site couldn't finish the export. Your files are saved here. Try Export again.", 14f);
                return;
            }
            work.pickupCode = receipt.code;
            work.pickupUrl = origin + (origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "/" : "/collect/index.html");
            SaveNotebook();
            if (NotebookOpen) FillSpread();
            string address = new Uri(origin).Authority;
            Caption(Crew.Fixer, "On your computer, go to " + address + "\nand type code " + receipt.code, 30f);
            nextLineAt = Mathf.Max(nextLineAt, Time.time + 30f);
            WriteInputLog("export complete: " + address + " code " + receipt.code + " work " + work.id, true);
            Voice.Enabled = !Scripted && Voiced;
            if (cast.TryGetValue(Crew.Fixer, out var fixer) && fixer != null)
                Voice.Speak(MakerRoles.VoiceOf(Crew.Fixer), fixer.transform,
                    "Your download code is " + receipt.code + ". The website and code are in your notebook.");
            AskMakeAnother(); // F98: Make another / Not now
        }


        [Serializable] private sealed class PickupReceipt { public string code, revision; }

        private IEnumerator WritePickupFiles(NotebookWork work, Transform thing, CoasterDesign design)
        {
            // Snapshot the current render mesh and paint. Keep worker tasks independent of scene objects.
            var filter = thing.GetComponentInChildren<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) yield break;
            var glbInput = GlbInput(thing, design);
            byte[] stl = null, mf = null;
            if (design.kind == "model")
            {
                yield return ReadTexture(design);
                var job = SolidJob(design, true);
                if (job == null) yield break;
                yield return RunJob(job);
                if (job.error != null || job.result == null || !job.result.Closed) yield break;
                var rebuilt = job.result;
                var painted = PaintedColours(thing.GetComponentInChildren<PaintableThing>(), design, rebuilt);
                var write = Task.Run(() =>
                {
                    var palette = PrintFiles.Reduce(rebuilt.colours, rebuilt.triangles, painted, PrintFiles.MaxPrintColours, out var index);
                    return (
                        PrintFiles.Stl(rebuilt.vertices, rebuilt.triangles, 1f, design.name),
                        PrintFiles.ThreeMf(rebuilt.vertices, rebuilt.triangles, index, palette, null, 1f, design.name));
                });
                while (!write.IsCompleted) yield return null;
                if (write.IsFaulted) yield break;
                (stl, mf) = write.Result;
            }
            else
            {
                Mesh printMesh = PrintOriented(mesh, design);
                stl = CoasterMesh.Stl(printMesh, thing.localScale.x, design.name);
                var paint = thing.GetComponentInChildren<PaintableThing>();
                if (paint != null && paint.Parts != null)
                {
                    var colours = paint.Parts.Select((part, index) =>
                        paint.Marks.TryGetValue(index, out var mark) && ColorUtility.TryParseHtmlString(mark.hex, out var tint)
                            ? tint : design.Tint(Color.white)).ToList();
                    mf = FitShapes.ThreeMf(printMesh, thing.localScale.x, paint.Parts, colours, design.name);
                }
                if (printMesh != mesh) Destroy(printMesh);
            }
            if (stl == null) yield break;
            var folder = Path.Combine(Application.persistentDataPath, "prints");
            var stem = Path.Combine(folder, work.id + "_" + Guid.NewGuid().ToString("N"));
            var writeFiles = Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(stem + ".stl", stl);
                if (mf != null) File.WriteAllBytes(stem + ".3mf", mf);
                if (glbInput != null) File.WriteAllBytes(stem + ".glb", PrintFiles.Glb(design.name,
                    glbInput.v, glbInput.n, glbInput.uv0, glbInput.UV1, glbInput.colours, glbInput.parts));
                if (glbInput != null)
                {
                    File.WriteAllBytes(stem + ".fbx", ModelExchange.Fbx(glbInput.v, glbInput.n, glbInput.uv0, glbInput.UV1, glbInput.parts));
                    File.WriteAllBytes(stem + ".zip", ModelExchange.Obj(glbInput.v, glbInput.n, glbInput.uv0, glbInput.UV1, glbInput.parts));
                }
            });
            while (!writeFiles.IsCompleted) yield return null;
            if (writeFiles.IsFaulted) yield break;
            work.stl = stem + ".stl";
            work.colourFile = mf != null ? stem + ".3mf" : null;
            work.glb = glbInput != null ? stem + ".glb" : null;
            work.fbx = glbInput != null ? stem + ".fbx" : null;
            work.obj = glbInput != null ? stem + ".zip" : null;
            SaveNotebook();
        }
    }
}
