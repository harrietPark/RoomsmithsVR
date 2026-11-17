using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Windups
{
    // F26: Meshy turns the approved preview picture into a textured 3D model.
    // Image to 3D (POST /openapi/v1/image-to-3d with the picture as a base64 data URI, then GET by
    // id), downloaded as OBJ + base colour texture. The STL for printing is written by the app
    // from that mesh at the measured size. Docs: https://docs.meshy.ai/en/api/image-to-3d
    public static class MeshyClient
    {
        public const string ImageEndpoint = "https://api.meshy.ai/openapi/v1/image-to-3d";
        public const string TextEndpoint = "https://api.meshy.ai/openapi/v2/text-to-3d";
        public const string RetextureEndpoint = "https://api.meshy.ai/openapi/v1/retexture";
        public static bool Available => WindupsDirectCredentials.TryGetMeshyKey(out _);

        // F33 (2026-11-11): two stages, so the shape is there in seconds and the colours follow.
        //  1. ImageToShape: the chosen picture -> an untextured mesh with smart topology (meshy-t2: median
        //     about 4 s, 5 credits; 100-15000 triangles).
        //  2. Retexture: that task's mesh painted after the same picture (keeps Meshy's UVs; 2K, median
        //     about 55 s, 10 credits). Docs (read 2026-11-11): /en/api/image-to-3d, /en/api/retexture.
        public const string ShapeModel = "meshy-t2";
        public const int ShapePolycount = 8000;

        public static IEnumerator ImageToShape(byte[] jpeg, Job job)
        {
            if (!WindupsDirectCredentials.TryGetMeshyKey(out string key)) { job.error = "Meshy is not set up in this build."; yield break; }
            var body = new JObject
            {
                ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
                ["model_type"] = "smart-topology", ["ai_model"] = ShapeModel, ["should_texture"] = false,
                ["target_polycount"] = ShapePolycount, ["target_formats"] = new JArray("obj")
            };
            yield return Run(ImageEndpoint, key, body, job, 1f);
        }

        public static IEnumerator Retexture(string shapeTaskId, byte[] styleJpeg, Job job)
        {
            if (!WindupsDirectCredentials.TryGetMeshyKey(out string key)) { job.error = "Meshy is not set up in this build."; yield break; }
            var body = new JObject
            {
                ["input_task_id"] = shapeTaskId,
                ["image_style_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(styleJpeg),
                ["enable_original_uv"] = true, ["texture_resolution"] = "2k", ["target_formats"] = new JArray("obj")
            };
            yield return Run(RetextureEndpoint, key, body, job, 2f);
        }

        // F84: a model of our own (the hand-shaped one, OBJ bytes) painted after the picture. Meshy takes it as
        // `model_url` (a data URI, at most 100 MB; docs read 2026-11-14) and makes its own UVs. The request body (a few
        // MB of base64) is built in a Task, never on the main thread.
        public static IEnumerator RetextureModel(byte[] obj, byte[] styleJpeg, Job job)
        {
            if (!WindupsDirectCredentials.TryGetMeshyKey(out string key)) { job.error = "Meshy is not set up in this build."; yield break; }
            if (obj == null || obj.Length == 0) { job.error = "No model to paint."; yield break; }
            if (obj.Length > 70 * 1024 * 1024) { job.error = "The shaped model is too big to send."; yield break; }
            var build = System.Threading.Tasks.Task.Run(() =>
            {
                var body = new JObject
                {
                    ["model_url"] = "data:application/octet-stream;base64," + Convert.ToBase64String(obj),
                    ["image_style_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(styleJpeg),
                    ["enable_original_uv"] = false, ["texture_resolution"] = "2k", ["target_formats"] = new JArray("obj")
                };
                return Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
            });
            while (!build.IsCompleted) yield return null;
            if (build.IsFaulted) { job.error = "Could not pack the model: " + build.Exception?.GetBaseException().Message; yield break; }
            yield return Run(RetextureEndpoint, key, null, job, 2f, build.Result);
        }

        public sealed class Job
        {
            public string id, status = "", error;
            public string obj;          // the downloaded OBJ text (F30 keeps it on the headset)
            public int progress;
            public Mesh mesh;
            public Texture2D texture;
            public float startedAt, finishedAt;   // realtime: the task was created / the model was read
            public bool Done => mesh != null || error != null;
            public float Seconds => finishedAt > 0f ? finishedAt - startedAt : 0f;
        }

        // A textured model of the object in `jpeg` (about 1 to 2 minutes; 15 credits with meshy-6-lite).
        public static IEnumerator ImageTo3D(byte[] jpeg, Job job)
        {
            if (!WindupsDirectCredentials.TryGetMeshyKey(out string key)) { job.error = "Meshy is not set up in this build."; yield break; }
            var body = new JObject
            {
                ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
                ["ai_model"] = "meshy-6-lite", ["should_texture"] = true, ["texture_resolution"] = "2k",
                ["should_remesh"] = true, ["target_polycount"] = 12000,
                ["target_formats"] = new JArray("obj"), ["origin_at"] = "bottom"
            };
            yield return Run(ImageEndpoint, key, body, job);
        }

        // A plain mesh from words (about 10 s; 5 credits with smart topology).
        public static IEnumerator TextTo3D(string prompt, Job job)
        {
            if (!WindupsDirectCredentials.TryGetMeshyKey(out string key)) { job.error = "Meshy is not set up in this build."; yield break; }
            var body = new JObject
            {
                ["mode"] = "preview", ["prompt"] = prompt.Length > 780 ? prompt.Substring(0, 780) : prompt,
                ["model_type"] = "smart-topology", ["ai_model"] = "meshy-t2",
                ["target_formats"] = new JArray("obj"), ["origin_at"] = "bottom"
            };
            yield return Run(TextEndpoint, key, body, job);
        }

        private static IEnumerator Run(string endpoint, string key, JObject body, Job job, float every = 3f, byte[] payload = null)
        {
            string created = null;
            yield return Send("POST", endpoint, key, payload ?? Encoding.UTF8.GetBytes(body.ToString()), text => created = text, error => job.error = error, payload != null ? 180 : 60);
            if (job.error != null) yield break;
            try { job.id = (string)JObject.Parse(created)["result"]; }
            catch (Exception exception) { job.error = "Meshy reply: " + exception.Message; yield break; }
            job.startedAt = Time.realtimeSinceStartup;

            float until = Time.realtimeSinceStartup + 600f;
            JObject task = null;
            while (Time.realtimeSinceStartup < until)
            {
                yield return new WaitForSecondsRealtime(every);
                string status = null;
                yield return Send("GET", endpoint + "/" + job.id, key, null, text => status = text, error => job.error = error);
                if (job.error != null) yield break;
                task = JObject.Parse(status);
                job.status = (string)task["status"] ?? "";
                job.progress = (int?)task["progress"] ?? 0;
                if (job.status == "SUCCEEDED") break;
                if (job.status == "FAILED" || job.status == "CANCELED")
                {
                    job.error = "Meshy could not make it: " + ((string)task["task_error"]?["message"] ?? job.status);
                    yield break;
                }
            }
            string objUrl = (string)task?["model_urls"]?["obj"];
            if (job.status != "SUCCEEDED" || objUrl == null) { job.error = job.error ?? "Meshy took too long."; yield break; }
            string obj = null;
            using (var download = UnityWebRequest.Get(objUrl))
            {
                download.timeout = 90;
                yield return download.SendWebRequest();
                if (download.result != UnityWebRequest.Result.Success) { job.error = "Download failed: " + download.error; yield break; }
                obj = download.downloadHandler.text;
            }
            string textureUrl = (string)task["texture_urls"]?.First?["base_color"];
            if (!string.IsNullOrEmpty(textureUrl))
                using (var download = UnityWebRequestTexture.GetTexture(textureUrl))
                {
                    download.timeout = 90;
                    yield return download.SendWebRequest();
                    if (download.result == UnityWebRequest.Result.Success) job.texture = DownloadHandlerTexture.GetContent(download);
                }
            job.obj = obj;
            try { job.mesh = ParseObj(obj); }
            catch (Exception exception) { job.error = "Could not read the model: " + exception.Message; }
            job.finishedAt = Time.realtimeSinceStartup;
        }

        private static IEnumerator Send(string method, string url, string key, byte[] json, Action<string> ok, Action<string> fail, int timeout = 60)
        {
            using (var http = new UnityWebRequest(url, method))
            {
                if (json != null) http.uploadHandler = new UploadHandlerRaw(json);
                http.downloadHandler = new DownloadHandlerBuffer();
                http.SetRequestHeader("Content-Type", "application/json");
                http.SetRequestHeader("Authorization", "Bearer " + key);
                http.timeout = timeout;
                yield return http.SendWebRequest();
                if (http.result == UnityWebRequest.Result.Success) ok(http.downloadHandler.text);
                else
                {
                    Debug.LogWarning("[Windups Meshy] " + method + " " + url + " -> " + http.responseCode + " " + http.downloadHandler?.text);
                    fail(http.responseCode == 401 ? "Meshy key was not accepted."
                    : http.responseCode == 402 ? "Meshy credits ran out."
                    : http.responseCode == 429 ? "Meshy is busy. Try again in a moment."
                    : "Meshy request failed (" + http.responseCode + ").");
                }
            }
        }

        // Minimal OBJ reader: positions, texture coordinates and faces (polygons fanned into
        // triangles). OBJ is right-handed, so x is mirrored and the winding reversed for Unity.
        // (F57: welded, with smooth normals; see MeshSmooth.)
        public static Mesh ParseObj(string text)
        {
            var source = new List<Vector3>();
            var uvSource = new List<Vector2>();
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            var face = new List<(int v, int t)>();
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("v ", StringComparison.Ordinal))
                {
                    var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    source.Add(new Vector3(-Float(p[1]), Float(p[2]), Float(p[3])));
                }
                else if (line.StartsWith("vt ", StringComparison.Ordinal))
                {
                    var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    uvSource.Add(new Vector2(Float(p[1]), p.Length > 2 ? Float(p[2]) : 0f));
                }
                else if (line.StartsWith("f ", StringComparison.Ordinal))
                {
                    var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    face.Clear();
                    for (int i = 1; i < p.Length; i++)
                    {
                        var parts = p[i].Split('/');
                        int v = int.Parse(parts[0], CultureInfo.InvariantCulture);
                        int t = parts.Length > 1 && parts[1].Length > 0 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
                        face.Add((v < 0 ? source.Count + v : v - 1, t == 0 ? -1 : t < 0 ? uvSource.Count + t : t - 1));
                    }
                    for (int i = 1; i + 1 < face.Count; i++)
                        foreach (var corner in new[] { face[0], face[i + 1], face[i] })
                        {
                            vertices.Add(source[corner.v]);
                            uvs.Add(corner.t >= 0 && corner.t < uvSource.Count ? uvSource[corner.t] : Vector2.zero);
                            triangles.Add(vertices.Count - 1);
                        }
                }
            }
            if (triangles.Count == 0) throw new InvalidOperationException("no faces");
            // F57: one vertex per corner gave one normal per face (a faceted, bumpy look). Corners are welded and
            // the normals smoothed across faces that meet gently; real edges (over 40 degrees) stay sharp.
            Windups.Handoff.MeshSmooth.Weld(vertices.ToArray(), uvs.ToArray(), triangles.ToArray(), Windups.Handoff.MeshSmooth.CreaseDegrees,
                out var welded, out var normals, out var weldedUv, out var weldedTriangles);
            var mesh = new Mesh { name = "MeshyModel", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = welded;
            mesh.normals = normals;
            if (weldedUv != null) mesh.uv = weldedUv;
            mesh.triangles = weldedTriangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        // F57: the OBJ's triangles as plain arrays (no Unity objects: usable off the main thread), x mirrored like
        // ParseObj. Used to measure how closed Meshy's own mesh is.
        public static void ParseObjArrays(string text, out Vector3[] vertices, out int[] triangles)
        {
            var source = new List<Vector3>();
            var tris = new List<int>();
            var face = new List<int>();
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("v ", StringComparison.Ordinal))
                {
                    var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    source.Add(new Vector3(-Float(p[1]), Float(p[2]), Float(p[3])));
                }
                else if (line.StartsWith("f ", StringComparison.Ordinal))
                {
                    var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    face.Clear();
                    for (int i = 1; i < p.Length; i++)
                    {
                        int v = int.Parse(p[i].Split('/')[0], CultureInfo.InvariantCulture);
                        face.Add(v < 0 ? source.Count + v : v - 1);
                    }
                    for (int i = 1; i + 1 < face.Count; i++) { tris.Add(face[0]); tris.Add(face[i + 1]); tris.Add(face[i]); }
                }
            }
            vertices = source.ToArray();
            triangles = tris.ToArray();
        }

        private static float Float(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
