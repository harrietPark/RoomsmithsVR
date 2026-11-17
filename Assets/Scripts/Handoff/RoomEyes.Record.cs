using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;

namespace Windups.Handoff
{
    // What Spark saw, kept on the headset for checking (persistentDataPath/eyes, the newest 8 looks):
    //   look_<stamp>.jpg      the picture with the first answer's boxes (thin red), what the zoomed-in check
    //                         took out (grey), the boxes used (green; orange = not placed, no tag), the thing
    //                         itself as the depth found it near those boxes (cyan) and where
    //                         each placed thing ended up in the room, drawn back into the picture (yellow:
    //                         foot to top, and its width at the foot). Yellow off the thing = the 3D place
    //                         is wrong; a box off the thing = the 2D box is wrong.
    //   look_<stamp>_raw.jpg  the picture as taken
    //   look_<stamp>.txt      the same in words
    //   look_<stamp>.json     the camera (pose, view), every depth point and the boxes: enough to place the
    //                         things again in the Editor (LoadLook + Replay, AgentScripts/ReplayLooks.cs).
    public sealed partial class RoomEyes
    {
        public static string LookFolder => Path.Combine(dataFolder ?? Application.persistentDataPath, "eyes");

        [Serializable] public sealed class LookRecord
        {
            public string stamp, question, reply, confirm, pose, timing;
            public int width, height;
            public float[] origin;        // the camera in the world
            public float[] rotation;      // x, y, z, w
            public float[] tan;           // the view's left, right, bottom, top at distance 1 in front of the camera
            public float pinholeError;    // degrees: how far a few test rays are from that simple camera
            public double[] depth;        // 9 numbers per point: viewport x, y, point x, y, z, normal x, y, z, owner (0 = grid, else the thing's number)
            public List<Placed> objects = new List<Placed>();
            public List<Placed> dropped = new List<Placed>();
        }

        [Serializable] public sealed class Placed
        {
            public string name;
            public float[] firstBox, box, fit;   // fit: the thing as the depth found it (null: placed from the box)
            public float match;
            public string[] colors;
            public bool focus, placed, onSurface, byDepth, big;
            public int owner;
            public float[] basePoint, top;
            public float width, height;
            public string note;
        }

        // A simple (pinhole) camera for a frame: rays and the way back from the world to the picture.
        public static void Pinhole(Frame frame, Vector3 origin, Quaternion rotation, float[] tan)
        {
            frame.cameraToWorld = Matrix4x4.TRS(origin, rotation, Vector3.one);
            frame.forward = rotation * Vector3.forward;
            frame.ray = v => new Ray(origin, rotation * new Vector3(Mathf.LerpUnclamped(tan[0], tan[1], v.x), Mathf.LerpUnclamped(tan[2], tan[3], v.y), 1f).normalized);
            var inverse = Quaternion.Inverse(rotation);
            frame.toViewport = world =>
            {
                Vector3 local = inverse * (world - origin);
                if (local.z <= 1e-5f) return new Vector2(-1f, -1f);
                return new Vector2((local.x / local.z - tan[0]) / (tan[1] - tan[0]), (local.y / local.z - tan[2]) / (tan[3] - tan[2]));
            };
        }

        private static float[] ViewOf(Frame frame, out Vector3 origin, out Quaternion rotation, out float error)
        {
            origin = frame.ray(new Vector2(.5f, .5f)).origin;
            rotation = frame.cameraToWorld.rotation;
            var inverse = Quaternion.Inverse(rotation);
            Vector3 Local(Vector2 v) => inverse * frame.ray(v).direction;
            Vector3 l = Local(new Vector2(0f, .5f)), r = Local(new Vector2(1f, .5f)), b = Local(new Vector2(.5f, 0f)), t = Local(new Vector2(.5f, 1f));
            var tan = new[] { l.x / l.z, r.x / r.z, b.y / b.z, t.y / t.z };
            error = 0f;
            foreach (var v in new[] { new Vector2(.2f, .8f), new Vector2(.85f, .3f), new Vector2(.5f, .5f), new Vector2(.05f, .05f) })
                error = Mathf.Max(error, Vector3.Angle(Local(v), new Vector3(Mathf.LerpUnclamped(tan[0], tan[1], v.x), Mathf.LerpUnclamped(tan[2], tan[3], v.y), 1f)));
            return tan;
        }

        // (Plain pixels, plain arithmetic and files only: runs on a worker thread.)
        private static void SaveLook(Frame frame, string question, string reply, string confirm, List<Seen> first, List<Seen> dropped, List<Found> found, string poseNote)
        {
            try
            {
                if (frame?.pixels == null) return;
                Directory.CreateDirectory(LookFolder);
                string stamp = DateTime.Now.ToString("MMdd-HHmmss");
                string path = Path.Combine(LookFolder, "look_" + stamp);
                if (frame.jpeg != null) File.WriteAllBytes(path + "_raw.jpg", frame.jpeg);
                File.WriteAllBytes(path + ".jpg", Overlay(frame, first, dropped, found));
                File.WriteAllText(path + ".txt",
                    "first answer\n" + string.Join("\n", first.Select(s => "  " + s.name + " box " + Numbers(s.firstBox ?? s.box))) +
                    "\nleft out by the zoomed-in check\n" + string.Join("\n", dropped.Select(s => "  " + s.name)) +
                    "\nplaced\n" + string.Join("\n", found.Select(f => "  " + f.seen.name + (f.placed ? "" : " NOT PLACED") + (f.byDepth ? " depth" : " plane") + " box " + Numbers(f.box) + (f.fit != null ? " fit " + Numbers(f.fit) : "") +
                        " base " + f.basePoint.ToString("F3") + " top " + f.top.ToString("F3") + " w " + f.width.ToString("F3") + (f.onSurface ? " on surface" : " in air") + " | " + f.note)) + "\n" + (frame.timing ?? "") + "\n");

                float[] tan = ViewOf(frame, out Vector3 origin, out Quaternion rotation, out float error);
                var record = new LookRecord
                {
                    stamp = stamp, question = question, reply = reply, confirm = confirm, pose = poseNote, timing = frame.timing, width = frame.size.x, height = frame.size.y,
                    origin = new[] { origin.x, origin.y, origin.z }, rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w }, tan = tan, pinholeError = error
                };
                var depth = frame.depth ?? new List<DepthPoint>();
                record.depth = Numbers(depth);
                foreach (var f in found)
                    record.objects.Add(new Placed
                    {
                        name = f.seen.name, firstBox = f.seen.firstBox, box = f.box, colors = f.seen.colors, focus = f.seen.focus, placed = f.placed, onSurface = f.onSurface,
                        byDepth = f.byDepth, big = f.big, owner = f.owner, fit = f.fit, match = f.match, basePoint = new[] { f.basePoint.x, f.basePoint.y, f.basePoint.z }, top = new[] { f.top.x, f.top.y, f.top.z },
                        width = f.width, height = f.height, note = f.note
                    });
                foreach (var s in dropped) record.dropped.Add(new Placed { name = s.name, firstBox = s.firstBox, box = s.box, colors = s.colors });
                File.WriteAllText(path + ".json", JsonConvert.SerializeObject(record, Formatting.None));

                // The newest 8 looks stay.
                var stamps = new DirectoryInfo(LookFolder).GetFiles("look_*.jpg").Select(f => Regex.Match(f.Name, @"^look_(\d{4}-\d{6})\.jpg$"))
                    .Where(m => m.Success).Select(m => m.Groups[1].Value).OrderByDescending(x => x).Skip(8).ToList();
                foreach (string old in stamps)
                    foreach (var file in new DirectoryInfo(LookFolder).GetFiles("look_" + old + "*")) file.Delete();
            }
            catch (Exception exception) { Debug.LogWarning("[Windups Eyes] look picture: " + exception.Message); }
        }

        private static string Numbers(float[] values) => string.Join(",", (values ?? new float[0]).Select(v => v.ToString("F3")));

        public const int DepthStride = 9;

        // Depth points as plain numbers (see LookRecord.depth), rounded to a tenth of a millimetre.
        public static double[] Numbers(List<DepthPoint> depth)
        {
            var numbers = new double[depth.Count * DepthStride];
            for (int i = 0; i < depth.Count; i++)
            {
                var d = depth[i];
                float[] values = { d.viewport.x, d.viewport.y, d.point.x, d.point.y, d.point.z, d.normal.x, d.normal.y, d.normal.z, d.owner };
                for (int k = 0; k < DepthStride; k++) numbers[i * DepthStride + k] = Math.Round(values[k], 4);
            }
            return numbers;
        }

        // The picture with the boxes and, for what was placed, its place in the room drawn back into it.
        public static byte[] Overlay(Frame frame, List<Seen> first, List<Seen> dropped, List<Found> found)
        {
            int w = frame.size.x, h = frame.size.y;
            var pixels = (Color32[])frame.pixels.Clone();
            void Dot(int x, int y, Color32 c, int thick)
            {
                for (int dx = 0; dx < thick; dx++)
                    for (int dy = 0; dy < thick; dy++)
                    {
                        int px = x + dx, py = y + dy;
                        if (px >= 0 && py >= 0 && px < w && py < h) pixels[(h - 1 - py) * w + px] = c;
                    }
            }
            void Line(Vector2 a, Vector2 b, Color32 c, int thick)
            {
                if (float.IsNaN(a.x) || float.IsNaN(b.x)) return;
                a = new Vector2(Mathf.Clamp(a.x, -w, 2 * w), Mathf.Clamp(a.y, -h, 2 * h));
                b = new Vector2(Mathf.Clamp(b.x, -w, 2 * w), Mathf.Clamp(b.y, -h, 2 * h));
                int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y))));
                for (int i = 0; i <= steps; i++)
                {
                    Vector2 p = Vector2.Lerp(a, b, i / (float)steps);
                    Dot(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), c, thick);
                }
            }
            void Box(float[] b, Color32 c, int thick)
            {
                if (b == null || b.Length != 4) return;
                float x0 = Mathf.Min(b[0], b[2]) * w, x1 = Mathf.Max(b[0], b[2]) * w - thick, y0 = Mathf.Min(b[1], b[3]) * h, y1 = Mathf.Max(b[1], b[3]) * h - thick;
                Line(new Vector2(x0, y0), new Vector2(x1, y0), c, thick); Line(new Vector2(x0, y1), new Vector2(x1, y1), c, thick);
                Line(new Vector2(x0, y0), new Vector2(x0, y1), c, thick); Line(new Vector2(x1, y0), new Vector2(x1, y1), c, thick);
            }
            Vector2 P(Vector3 world)
            {
                Vector2 v = frame.toViewport(world);
                return v.x < -.5f && v.y < -.5f ? new Vector2(float.NaN, float.NaN) : new Vector2(v.x * w, (1f - v.y) * h);
            }
            if (first != null) foreach (var s in first) Box(s.firstBox ?? s.box, new Color32(230, 40, 40, 255), 1);
            if (dropped != null) foreach (var s in dropped) Box(s.firstBox ?? s.box, new Color32(150, 150, 150, 255), 2);
            Vector3 eye = frame.Eye;
            foreach (var f in found)
            {
                Box(f.box, f.placed ? new Color32(40, 220, 90, 255) : new Color32(255, 140, 0, 255), 3);
                if (f.fit != null) Box(f.fit, new Color32(0, 230, 255, 255), 2);
                if (!f.placed) continue;
                var yellow = new Color32(255, 235, 0, 255);
                Vector3 facing = Vector3.ProjectOnPlane(f.basePoint - eye, Vector3.up);
                Vector3 side = facing.sqrMagnitude < 1e-6f ? Vector3.right : Vector3.Cross(Vector3.up, facing.normalized);
                Line(P(f.basePoint), P(f.top), yellow, 3);
                Line(P(f.basePoint - side * f.width * .5f), P(f.basePoint + side * f.width * .5f), yellow, 3);
                Line(P(f.top - side * .01f), P(f.top + side * .01f), yellow, 3);
            }
            return Jpeg(pixels, w, h, 80);
        }

        // ------------------------------------------------------------ replay (Editor)

        // A saved look as a frame again: the same rays and depth points (and the picture, when its _raw.jpg
        // lies next to the json).
        public static Frame LoadLook(string jsonPath, out LookRecord record)
        {
            record = JsonConvert.DeserializeObject<LookRecord>(File.ReadAllText(jsonPath));
            var frame = new Frame { size = new Vector2Int(record.width, record.height), depth = new List<DepthPoint>() };
            Pinhole(frame, new Vector3(record.origin[0], record.origin[1], record.origin[2]),
                new Quaternion(record.rotation[0], record.rotation[1], record.rotation[2], record.rotation[3]), record.tan);
            var d = record.depth ?? new double[0];
            for (int i = 0; i + DepthStride - 1 < d.Length; i += DepthStride)
                frame.depth.Add(new DepthPoint
                {
                    viewport = new Vector2((float)d[i], (float)d[i + 1]), point = new Vector3((float)d[i + 2], (float)d[i + 3], (float)d[i + 4]),
                    normal = new Vector3((float)d[i + 5], (float)d[i + 6], (float)d[i + 7]), owner = (int)Math.Round(d[i + 8])
                });
            string raw = jsonPath.Substring(0, jsonPath.Length - ".json".Length) + "_raw.jpg";
            if (File.Exists(raw))
            {
                frame.jpeg = File.ReadAllBytes(raw);
                frame.texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                frame.texture.LoadImage(frame.jpeg);
                frame.size = new Vector2Int(frame.texture.width, frame.texture.height);
                frame.pixels = frame.texture.GetPixels32();
            }
            return frame;
        }

        // The saved boxes placed again with today's rules.
        public static List<Found> Replay(Frame frame, LookRecord record)
        {
            var found = new List<Found>();
            foreach (var o in record.objects)
            {
                if (o.box == null || o.box.Length != 4) continue;
                var f = new Found
                {
                    seen = new Seen { name = o.name, box = (float[])o.box.Clone(), firstBox = o.firstBox, colors = o.colors, focus = o.focus },
                    box = (float[])o.box.Clone(), byDepth = frame.depth.Count >= 40, owner = o.owner
                };
                found.Add(f);
            }
            foreach (var f in found)
                if (f.byDepth) PlaceByDepth(frame, f, found); else f.note = "NOT PLACED: the look has no depth";
            Untangle(found);
            return found;
        }
    }
}
