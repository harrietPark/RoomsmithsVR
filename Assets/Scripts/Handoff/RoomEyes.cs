using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oculus.Interaction.Input;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // F26: Scout's eyes. Takes a passthrough camera picture (Quest 3 / 3S, MRUK PassthroughCameraAccess,
    // horizonos.permission.HEADSET_CAMERA), asks the vision model what objects are in it (name, box,
    // colours, surface), and places each one in the room.
    //  - The picture and its camera pose belong to the same camera image (the GPU texture is copied on
    //    the frame after the image arrived), and the pose is carried into the world through the rig's
    //    tracking space when MRUK's world lock has moved it.
    //  - Placement uses the headset's depth (MRUK EnvironmentRaycastManager, switched on only while
    //    looking): depth sampled across the picture right after it is taken, plus fresh rays through the
    //    object's box while the head still looks the same way. The object's own surface inside its box
    //    gives where it is; the desk just below the box gives its base height.
    //  - Without depth (no support, nothing hit): a ray through the bottom of its box meets the real desk
    //    (MRUK room colliders), and rays through its sides give an estimated width.
    // In the Editor the head camera stands in for the passthrough camera (and, for tests, colliders for
    // the depth sensor: SimulateDepth).
    // 2026-11-11 (7th Quest try: tags on things that were not there, half of the desk things put on the
    // floor): the model's first answer is checked with a second, zoomed-in look (RoomEyes.Look/Confirm),
    // the placement rules are in RoomEyes.Place.cs (what a thing stands on is found from the levels seen
    // in its box, and a placement that does not agree with the picture gets no tag: Found.placed), and
    // every look is saved with its depth so it can be replayed in the Editor (RoomEyes.Record.cs).
    // 2026-11-12 (frames were dropped while Spark looked; the app must never fall under 60 fps): nothing
    // heavy runs on the main thread any more. The picture comes back from the GPU without waiting for it
    // (AsyncGPUReadback) as plain pixels (Frame.pixels); packing JPEGs, the zoomed pictures, the requests'
    // text, the colour box, the placement and the saved look all run on worker threads (the frame's rays
    // are made plain arithmetic for that: Simplify). Depth rays, which only the main thread can cast, stop
    // for the frame after DepthBudgetMs. Each look writes its worst frame into its record (Frame.timing).
    public sealed partial class RoomEyes : MonoBehaviour
    {
        public const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";

        public sealed class Frame
        {
            public byte[] jpeg;
            public Vector2Int size;
            public Matrix4x4 cameraToWorld, projection;
            // Normalised viewport point (0,0 bottom-left) -> world ray from the camera.
            public Func<Vector2, Ray> ray;
            public Func<Vector3, Vector2> toViewport;   // world point -> viewport (0-1, origin bottom-left)
            // The picture as plain pixels, bottom row first (what everything here works on, also off the
            // main thread). `texture` holds the same for those who read a Texture2D; it is never sent to the GPU.
            public Color32[] pixels;
            public Texture2D texture;
            public Vector3 forward;                     // the camera's forward for this picture
            public List<DepthPoint> depth;              // depth sampled with the picture (null without depth)
            public bool depthPending;
            public volatile bool depthStop;             // stop sampling (the look goes on with what there is)
            public int depthFrames;                     // how many frames the sampling was spread over
            public bool plain;                          // ray / toViewport are plain arithmetic (safe on any thread)
            public string timing;                       // how the look went for the frame rate (in its record)
            public int boxes;                           // how many things have been placed in this picture
            public Vector3 headPosition, headForward;   // the head when the picture was taken (is it still there?)
            public Vector3 Eye => cameraToWorld.GetColumn(3);
        }

        public struct DepthPoint
        {
            public Vector2 viewport;                    // origin bottom-left
            public Vector3 point, normal;
            // 0: the grid over the whole picture. Else: an extra ray through that thing's box (Found.owner);
            // only that thing uses it, so one thing's rays never change how its neighbour is placed.
            public int owner;
        }

        [Serializable] public sealed class Seen
        {
            public string name;
            public float[] box;          // x0, y0, x1, y1 in 0-1, origin top-left
            public string[] colors;      // #rrggbb
            public string surface;
            public bool focus;           // what the person means / looks at
            [JsonIgnore] public float[] firstBox;   // the box of the first answer (before the zoomed-in check)
        }

        public sealed class Found
        {
            public Seen seen;
            public Vector3 basePoint;    // where it stands (on the desk)
            public Vector3 top;
            public float width;          // metres, estimated
            public float height;
            public bool onSurface;
            public bool byDepth;         // placed from the headset's depth (else from the desk plane)
            // Its place in the room agrees with the picture. False: seen and named, but nothing may be
            // shown on it or done at its place (basePoint and top are then only a guess).
            public bool placed;
            public bool big;             // a monitor, a laptop, furniture: placed where its box's middle is
            public int owner;            // its number in the picture (its own extra depth rays carry it)
            public string note;          // how it was placed, or why not
            public List<DepthPoint> body = new List<DepthPoint>();   // the depth points taken as the thing itself
            public byte[] crop;          // just the object, for colour and texture
            public float[] box;          // the model's box (after the check and Refine), 0-1, origin top-left
            // The thing itself as the depth sees it, in the picture (same coordinates): the model's boxes are
            // often off by half the thing, so when something stands where they roughly point, that is taken
            // (null: nothing found by depth, the place comes from the box). `match`: how well it lies over the boxes.
            public float[] fit;
            public float match;
        }

        public WindupsArrival arrival;
        public string LastError { get; private set; }
        public bool Looking { get; private set; }
        public Frame LastFrame { get; private set; }
        public List<Found> LastFound { get; } = new List<Found>();
        // What the first answer listed and the zoomed-in check took out again (not there, cut off, a hand).
        public List<Seen> LastDropped { get; } = new List<Seen>();
        // Tests: the vision reply to use instead of the model (Editor scripted runs), and the reply of the
        // zoomed-in check (null: scripted runs skip the check).
        public string ScriptedReply { get; set; }
        public string ScriptedConfirm { get; set; }
        public const int MaxObjects = 5;

        private Meta.XR.PassthroughCameraAccess camera;
        private OVRCameraRig rig;
        private GameObject depthHolder;
#if UNITY_ANDROID && !UNITY_EDITOR
        private Meta.XR.EnvironmentRaycastManager depthRays;
#endif
        // About 1 cm between samples on a desk 60 cm away (a mug gets some 60 points, a pill bottle 30,
        // before the extra rays around its box). Taken with the picture, so it matches what the picture
        // shows whatever the head does afterwards.
        public const int DepthCols = 96, DepthRows = 72;
        // Editor tests: colliders stand in for the headset's depth sensor (only the layers in the mask:
        // real depth sees the room and real things, never the characters or cards).
        public static bool SimulateDepth { get; set; }
        public static int SimulatedDepthMask { get; set; } = Physics.DefaultRaycastLayers;
        public static float SimulatedDepthNoise { get; set; }   // metres, like the headset's depth noise
        public string LastPoseNote { get; private set; }
        public int LastDepthSamples { get; private set; }
        public string LastLocateNote { get; private set; }
        public List<DepthPoint> LastBody { get; } = new List<DepthPoint>();

        private bool Scripted => arrival != null && arrival.handoff.meeting.LocalDemoEnabled;

        public static bool OnDevice => Application.platform == RuntimePlatform.Android;
        public static bool HasPermission =>
#if UNITY_ANDROID && !UNITY_EDITOR
            UnityEngine.Android.Permission.HasUserAuthorizedPermission(CameraPermission);
#else
            true;
#endif

        // Start the camera early (when the maker session starts) so it is ready when Spark looks. Only once
        // the camera is allowed (asked with the others at start; Capture asks again when Spark needs it).
        public void Prepare()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (camera == null && HasPermission)
            {
                var go = new GameObject("Scout Camera");
                go.transform.SetParent(transform, false);
                go.SetActive(false);
                camera = go.AddComponent<Meta.XR.PassthroughCameraAccess>();
                camera.CameraPosition = Meta.XR.PassthroughCameraAccess.CameraPositionType.Left;
                camera.RequestedResolution = new Vector2Int(1280, 960);
                go.SetActive(true);
            }
#endif
        }

        // ------------------------------------------------------------ picture

        // `depth`: also sample the headset's depth across the picture (for placing what is in it).
        public IEnumerator Capture(Action<Frame> done, bool depth = false)
        {
            LastError = null;
            EnsurePaths();
            var frame = new Frame();
            Texture source;
            RenderTexture rendered = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!HasPermission) yield return Windups.WindupsPermissions.Ensure(CameraPermission, null);
            if (!HasPermission)
            {
                LastError = Windups.WindupsPermissions.Blocked(CameraPermission) ? "Allow the headset camera in the app's permissions so I can see." : "I need the camera to see.";
                done(null);
                yield break;
            }
            Prepare();
            float until = Time.realtimeSinceStartup + 6f;
            while ((camera == null || !camera.IsPlaying) && Time.realtimeSinceStartup < until) yield return null;
            if (camera == null || !camera.IsPlaying) { LastError = "The camera didn't start."; done(null); yield break; }
            // The texture is updated on the render thread just before display, so a copy made now holds
            // the previous image. Wait for a frame that brought a new image, keep that image's pose, and
            // copy on the next frame if no newer image came in between: the picture and the pose match.
            Pose pose = WorldPose(camera.GetCameraPose());
            for (int tries = 0; tries < 6; tries++)
            {
                float wait = Time.realtimeSinceStartup + .4f;
                while (!camera.IsUpdatedThisFrame && Time.realtimeSinceStartup < wait) yield return null;
                pose = WorldPose(camera.GetCameraPose());
                yield return null;
                if (!camera.IsUpdatedThisFrame) break;
            }
            Debug.Log("[Windups Eyes] Camera pose " + LastPoseNote);
            source = camera.GetTexture();
            var access = camera;
            frame.size = camera.CurrentResolution;
            frame.cameraToWorld = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);
            frame.forward = pose.rotation * Vector3.forward;
            frame.ray = v => access.ViewportPointToRay(v, pose);
            frame.toViewport = w => access.WorldToViewportPoint(w, pose);
#else
            var head = Camera.main;
            if (head == null) { LastError = "No camera."; done(null); yield break; }
            yield return new WaitForEndOfFrame();
            // Same shape as the view, so the camera's own projection maps the picture back to rays.
            var size = new Vector2Int(960, Mathf.Max(2, Mathf.RoundToInt(960f / Mathf.Max(.1f, head.aspect))));
            var rt = RenderTexture.GetTemporary(size.x, size.y, 24);
            var old = head.targetTexture;
            // A real camera never sees the app's own mic (or its pill): leave it out of the stand-in picture.
            var hidden = new List<GameObject>();
            if (arrival != null)
                foreach (var go in new[] { arrival.microphoneButton, arrival.microphoneCube })
                    if (go != null && go.activeSelf) { go.SetActive(false); hidden.Add(go); }
            // F30: nor anything else the app draws (the session's notes, rings, made things, the crew):
            // the stand-in picture shows only the room and the test's stand-in objects, like the real camera.
            var unseen = new List<Renderer>();
            var unseenCanvases = new List<Canvas>(); // F56: world-space pictures and labels too (photo cards hung in the picture)
            void Hide(Transform root)
            {
                if (root == null) return;
                foreach (var r in root.GetComponentsInChildren<Renderer>()) if (r.enabled) { r.enabled = false; unseen.Add(r); }
                foreach (var c in root.GetComponentsInChildren<Canvas>()) if (c.enabled) { c.enabled = false; unseenCanvases.Add(c); }
            }
            if (arrival != null)
            {
                Hide(arrival.transform);
                foreach (var dock in arrival.handoff.docks) if (dock != null && dock.character != null) Hide(dock.character.transform);
            }
            head.targetTexture = rt;
            head.Render();
            head.targetTexture = old;
            foreach (var r in unseen) if (r != null) r.enabled = true;
            foreach (var c in unseenCanvases) if (c != null) c.enabled = true;
            foreach (var go in hidden) go.SetActive(true);
            rendered = rt;
            source = rt;
            frame.size = size;
            if (!LightInRenderedPictures) NoLight(frame); // F69: a rendered stand-in picture has no room light to take out
            // Freeze the camera as it was for this picture.
            var toWorld = head.cameraToWorldMatrix;
            var inverse = (head.projectionMatrix).inverse;
            Vector3 origin = head.transform.position;
            frame.cameraToWorld = head.transform.localToWorldMatrix;
            frame.projection = head.projectionMatrix;
            frame.forward = head.transform.forward;
            frame.ray = v =>
            {
                Vector4 clip = new Vector4(v.x * 2f - 1f, v.y * 2f - 1f, 1f, 1f);
                Vector4 view = inverse * clip;
                Vector3 dir = toWorld.MultiplyVector(new Vector3(view.x, view.y, view.z) / view.w).normalized;
                return new Ray(origin, dir);
            };
            var worldToClip = head.projectionMatrix * head.worldToCameraMatrix;
            frame.toViewport = w =>
            {
                Vector4 c = worldToClip * new Vector4(w.x, w.y, w.z, 1f);
                return c.w <= 0f ? new Vector2(-1f, -1f) : new Vector2(c.x / c.w * .5f + .5f, c.y / c.w * .5f + .5f);
            };
#endif
            var headNow = Camera.main;
            frame.headPosition = headNow != null ? headNow.transform.position : frame.Eye;
            frame.headForward = headNow != null ? headNow.transform.forward : frame.forward;
            Simplify(frame);
            // The depth is sampled from now on, while the picture is on its way back from the GPU.
            if (depth && DepthAvailable) StartCoroutine(SampleDepth(frame));
            yield return Fill(frame, source);
            if (rendered != null) RenderTexture.ReleaseTemporary(rendered);
            if (frame.pixels == null || frame.jpeg == null)
            {
                frame.depthStop = true;
                LastError = "The camera picture did not come.";
                done(null);
                yield break;
            }
            if (LastFrame?.texture != null) Destroy(LastFrame.texture);
            LastFrame = frame;
            done(frame);
        }

        // The camera of a frame as plain arithmetic (a simple camera: its place, its turn and the edges of
        // its view), when that is what it is (within a twentieth of a degree: the headset's camera and the
        // Editor's both are). Then rays and the way back to the picture can be worked out on any thread.
        private static void Simplify(Frame frame)
        {
            float[] tan = ViewOf(frame, out Vector3 origin, out Quaternion rotation, out float error);
            if (error > .05f || float.IsNaN(error)) return;
            Vector3 forward = frame.forward;
            Pinhole(frame, origin, rotation, tan);
            frame.forward = forward;
            frame.plain = true;
        }

        // The picture off the GPU without waiting for it (the old ReadPixels stopped the frame until the GPU
        // was done), then packed as a JPEG on a worker thread.
        private IEnumerator Fill(Frame frame, Texture source)
        {
            int w = frame.size.x, h = frame.size.y;
            var rt = RenderTexture.GetTemporary(w, h, 0);
            Graphics.Blit(source, rt);
            Color32[] pixels = null;
            bool flip = false;
            if (SystemInfo.supportsAsyncGPUReadback && readbackRows == 0) yield return CheckReadback();
            if (SystemInfo.supportsAsyncGPUReadback && readbackRows > 0)
            {
                flip = readbackRows == 2;
                var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
                float until = Time.realtimeSinceStartup + 2f;
                while (!request.done && Time.realtimeSinceStartup < until) yield return null;
                if (request.done && !request.hasError)
                {
                    var data = request.GetData<Color32>();
                    if (data.Length == w * h)
                    {
                        pixels = new Color32[w * h];
                        data.CopyTo(pixels);
                    }
                }
            }
            if (pixels == null)
            {
                // No readback on this device (or it failed): the old way, which waits for the GPU.
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                var read = new Texture2D(w, h, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                RenderTexture.active = previous;
                pixels = read.GetPixels32();
                flip = false;
                Destroy(read);
            }
            RenderTexture.ReleaseTemporary(rt);
            var packing = Task.Run(() =>
            {
                if (flip)
                {
                    var row = new Color32[w];
                    for (int y = 0; y < h / 2; y++)
                    {
                        Array.Copy(pixels, y * w, row, 0, w);
                        Array.Copy(pixels, (h - 1 - y) * w, pixels, y * w, w);
                        Array.Copy(row, 0, pixels, (h - 1 - y) * w, w);
                    }
                }
                return Jpeg(pixels, w, h, 80);
            });
            while (!packing.IsCompleted) yield return null;
            if (packing.Status == TaskStatus.RanToCompletion) frame.jpeg = packing.Result;
            else Debug.LogWarning("[Windups Eyes] picture: " + packing.Exception?.GetBaseException().Message);
            frame.pixels = pixels;
            frame.texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            frame.texture.SetPixelData(pixels, 0);   // its own memory only: nothing is sent to the GPU
        }

        // Which way the rows of a readback run on this device, found out once with a two-row picture:
        // 1 = bottom row first (like Texture2D), 2 = top row first (Fill turns the picture over), -1 = no
        // readback here (Fill waits for the GPU the old way).
        private static int readbackRows;
        public static int ReadbackRows => readbackRows;

        private IEnumerator CheckReadback()
        {
            var probe = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            probe.SetPixels32(new[] { new Color32(255, 0, 0, 255), new Color32(255, 0, 0, 255), new Color32(0, 0, 255, 255), new Color32(0, 0, 255, 255) });   // bottom row red, top row blue
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
            Debug.Log("[Windups Eyes] readback rows: " + (readbackRows == 1 ? "bottom first" : readbackRows == 2 ? "top first (turned over)" : "no readback, waiting for the GPU instead"));
        }

        // Pixels (bottom row first) as a JPEG. Safe on any thread.
        public static byte[] Jpeg(Color32[] pixels, int width, int height, int quality) =>
            ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height, 0, quality);

        private static IEnumerator Wait(Task task)
        {
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) Debug.LogWarning("[Windups Eyes] " + task.Exception?.GetBaseException());
        }

        // Paths the worker threads write to (Unity gives them out on the main thread only).
        private static string dataFolder;
        private static void EnsurePaths() { dataFolder ??= Application.persistentDataPath; }

        // "#rrggbb" or "#rgb" (the model's colours). Safe on any thread.
        public static bool TryColour(string html, out Color colour)
        {
            colour = Color.black;
            if (string.IsNullOrEmpty(html)) return false;
            string hex = html.Trim().TrimStart('#');
            if (hex.Length == 3) hex = "" + hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2];
            if (hex.Length < 6 || !int.TryParse(hex.Substring(0, 6), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int rgb)) return false;
            colour = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
            return true;
        }

        // GetCameraPose is the native tracking pose. When MRUK's world lock (or anything else) has moved the
        // rig's tracking space, that pose has to be carried into the world like the rig's own eyes. Of the
        // raw and the carried pose, the one nearer the head camera right now is the world pose.
        private Pose WorldPose(Pose raw)
        {
            if (rig == null) rig = FindFirstObjectByType<OVRCameraRig>();
            var space = rig != null ? rig.trackingSpace : null;
            var eye = Camera.main != null ? Camera.main.transform : null;
            if (space == null || eye == null) { LastPoseNote = "raw (no rig)"; return raw; }
            var carried = new Pose(space.TransformPoint(raw.position), space.rotation * raw.rotation);
            float Error(Pose p) => Vector3.Distance(p.position, eye.position) + Quaternion.Angle(p.rotation, eye.rotation) * .004f;
            float rawError = Error(raw), carriedError = Error(carried);
            bool useCarried = carriedError + .005f < rawError;
            LastPoseNote = (useCarried ? "carried" : "raw") + " (raw " + rawError.ToString("F3") + ", carried " + carriedError.ToString("F3") + ")";
            return useCarried ? carried : raw;
        }

        // ------------------------------------------------------------ depth

        private bool DepthAvailable
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return Meta.XR.EnvironmentRaycastManager.IsSupported;
#else
                return SimulateDepth;
#endif
            }
        }

        private bool DepthLive => DepthAvailable && (depthHolder != null ? depthHolder.activeSelf : SimulateDepth);

        // The depth raycaster costs GPU time, so it runs only while Spark looks.
        public void UseDepth(bool on)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Meta.XR.EnvironmentRaycastManager.IsSupported) return;
            if (depthHolder == null)
            {
                if (!on) return;
                depthHolder = new GameObject("Scout Depth");
                depthHolder.SetActive(false);
                depthHolder.transform.SetParent(transform, false);
                depthRays = depthHolder.AddComponent<Meta.XR.EnvironmentRaycastManager>();
            }
            if (depthHolder.activeSelf != on) depthHolder.SetActive(on);
#endif
        }

        private bool DepthRay(Ray ray, out Vector3 point, out Vector3 normal, out bool notReady)
        {
            point = normal = Vector3.zero;
            notReady = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (depthRays == null || !depthRays.isActiveAndEnabled) { notReady = true; return false; }
            bool ok = depthRays.Raycast(ray, out var hit, 4f);
            notReady = hit.status == Meta.XR.EnvironmentRaycastHitStatus.NotReady;
            point = hit.point;
            normal = hit.normal;
            return ok;
#else
            if (!SimulateDepth || !Physics.Raycast(ray, out var hit, 4f, SimulatedDepthMask, QueryTriggerInteraction.Ignore)) return false;
            point = hit.point + ray.direction * (Gauss() * SimulatedDepthNoise);
            normal = (hit.normal + UnityEngine.Random.insideUnitSphere * (SimulatedDepthNoise * 20f)).normalized;
            return true;
#endif
        }

        // Milliseconds of depth rays a frame (they can only be cast on the main thread; the rest of the
        // frame has to stay within 60 fps).
        public static float DepthBudgetMs = 2f;

        // A grid of depth rays across the picture, skipping the person's hands: every third row first, then
        // the rows between (if the look has to go on early, what there is covers the whole picture), and
        // only DepthBudgetMs of them a frame.
        private IEnumerator SampleDepth(Frame frame)
        {
            frame.depthPending = true;
            frame.depth = new List<DepthPoint>();
            UseDepth(true);
            float until = Time.realtimeSinceStartup + 2.5f;
            while (Time.realtimeSinceStartup < until && !frame.depthStop)
            {
                DepthRay(frame.ray(new Vector2(.5f, .5f)), out _, out _, out bool notReady);
                if (!notReady) break;
                yield return null;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var hands = HandPoints();
            int frames = 1;
            for (int pass = 0; pass < 3 && !frame.depthStop; pass++)
                for (int row = pass; row < DepthRows && !frame.depthStop; row += 3)
                    for (int col = 0; col < DepthCols; col++)
                    {
                        var v = new Vector2((col + .5f) / DepthCols, (row + .5f) / DepthRows);
                        if (DepthRay(frame.ray(v), out var point, out var normal, out _) && !NearHands(point, hands))
                            frame.depth.Add(new DepthPoint { viewport = v, point = point, normal = normal });
                        if ((col & 7) == 7 && clock.Elapsed.TotalMilliseconds > DepthBudgetMs)
                        {
                            yield return null;
                            if (frame.depthStop) break;
                            frames++;
                            hands = HandPoints();
                            clock.Restart();
                        }
                    }
            LastDepthSamples = frame.depth.Count;
            frame.depthFrames = frames;
            frame.depthPending = false;
        }

        private static readonly HandJointId[] HandJoints =
        {
            HandJointId.HandWristRoot, HandJointId.HandPalm, HandJointId.HandThumbTip, HandJointId.HandIndexTip, HandJointId.HandMiddleTip,
            HandJointId.HandRingTip, HandJointId.HandPinkyTip, HandJointId.HandIndex1, HandJointId.HandMiddle1, HandJointId.HandThumb2
        };

        private static List<Vector3> HandPoints()
        {
            var points = new List<Vector3>();
            foreach (var hand in HandSet.Both())
                if (hand != null && hand.isActiveAndEnabled && hand.IsConnected)
                {
                    foreach (var joint in HandJoints)
                        if (hand.GetJointPose(joint, out Pose pose)) points.Add(pose.position);
                    // The forearm too (an arm in the picture was taken for the thing behind it): from the
                    // wrist away from the palm, as far as an elbow.
                    if (hand.GetJointPose(HandJointId.HandWristRoot, out Pose wrist) && hand.GetJointPose(HandJointId.HandPalm, out Pose palm))
                        points.AddRange(Forearm(wrist.position, palm.position));
                }
            return points;
        }

        public static IEnumerable<Vector3> Forearm(Vector3 wrist, Vector3 palm)
        {
            Vector3 along = wrist - palm;
            if (along.sqrMagnitude < 1e-6f) yield break;
            along.Normalize();
            for (float d = .06f; d <= .31f; d += .06f) yield return wrist + along * d;
        }

        private static float Gauss() => Mathf.Sqrt(-2f * Mathf.Log(Mathf.Max(1e-6f, UnityEngine.Random.value))) * Mathf.Cos(2f * Mathf.PI * UnityEngine.Random.value);

        private static bool NearHands(Vector3 point, List<Vector3> hands)
        {
            if (hands == null) return false;
            foreach (var h in hands) if ((h - point).sqrMagnitude < .08f * .08f) return true;
            return false;
        }

        private static Vector3 Median(List<Vector3> points)
        {
            float M(Func<Vector3, float> axis) { var v = points.Select(axis).OrderBy(x => x).ToList(); return v[v.Count / 2]; }
            return new Vector3(M(p => p.x), M(p => p.y), M(p => p.z));
        }

        // ------------------------------------------------------------ what is in it

        // `question` says what the person means ("the mug", "this yellow thing"); the matching object
        // (or the one in the middle of the view) comes back with focus = true.
        // Two looks: the whole picture (what is there, roughly where), then each listed thing zoomed in
        // (is it really there, what is it, its exact box). What the second look does not confirm is left
        // out; what cannot be placed in the room stays in the list (name, colours, crop) with placed = false.
        public IEnumerator Look(string question, Action<List<Found>> done)
        {
            Looking = true;
            LastFound.Clear();
            LastDropped.Clear();
            EnsurePaths();
            var watch = new FrameWatch();
            lastWatch = watch;
            StartCoroutine(WatchFrames(watch));
            Frame frame = null;
            UseDepth(true);   // warms up while the picture is taken
            yield return Capture(f => frame = f, true);
            watch.phase = "first answer";
            if (frame == null) { watch.on = false; UseDepth(false); Looking = false; done(LastFound); yield break; }
            bool scripted = Scripted || ScriptedReply != null;
            string json = null;
            if (scripted)
            {
                yield return new WaitForSeconds(.3f);
                json = ScriptedReply ?? "{\"objects\":[]}";
            }
            else
            {
                // The request's text (the picture as base64 in JSON) is put together on a worker thread.
                byte[] jpeg = frame.jpeg;
                var asking = Task.Run(() => Encoding.UTF8.GetBytes(VisionRequest(question, jpeg)));
                yield return Wait(asking);
                if (asking.Status == TaskStatus.RanToCompletion) yield return Ask(asking.Result, r => json = r);
                else LastError = "I couldn't send the picture.";
            }
            if (json != null)
            {
                Debug.Log("[Windups Eyes] " + json);
                var first = (JObject.Parse(json)["objects"]?.ToObject<List<Seen>>() ?? new List<Seen>())
                    .Where(s => s != null && s.box != null && s.box.Length == 4 && Mathf.Abs(s.box[2] - s.box[0]) > .008f && Mathf.Abs(s.box[3] - s.box[1]) > .008f)
                    .Take(MaxObjects).ToList();
                foreach (var s in first) s.firstBox = (float[])s.box.Clone();
                var seen = first;
                string confirm = null;
                if (first.Count > 0 && (!scripted || ScriptedConfirm != null))
                {
                    watch.phase = "zoomed check";
                    if (scripted) confirm = ScriptedConfirm;
                    else
                    {
                        // The zoomed, ruled pictures (a megapixel each to cut out, enlarge and pack) and the
                        // request around them: on a worker thread.
                        var asking = Task.Run(() => Encoding.UTF8.GetBytes(ConfirmRequest(frame, first)));
                        yield return Wait(asking);
                        if (asking.Status == TaskStatus.RanToCompletion) yield return Ask(asking.Result, r => confirm = r);
                    }
                    if (confirm != null)
                    {
                        Debug.Log("[Windups Eyes] check " + confirm);
                        seen = Confirmed(frame, first, confirm, LastDropped);
                    }
                    else LastError = null;   // the check did not come back: the first answer stands
                }
                // The depth grid is done by now (the model took seconds); if not, go on with what there is.
                watch.phase = "depth";
                float depthUntil = Time.realtimeSinceStartup + 4f;
                while (frame.depthPending && Time.realtimeSinceStartup < depthUntil) yield return null;
                frame.depthStop = true;
                while (frame.depthPending) yield return null;
                // Each thing's box (tightened by its colour) and its picture: on a worker thread.
                List<Found> things = null;
                var starting = Task.Run(() => seen.Select(s => Begin(frame, s)).ToList());
                yield return Wait(starting);
                things = starting.Status == TaskStatus.RanToCompletion ? starting.Result : new List<Found>();
                // More depth around each thing (main thread, a little a frame), then its place.
                if (DepthLive && HeadStill(frame))
                    foreach (var thing in things) yield return AddBoxDepth(frame, thing);
                watch.phase = "placing";
                if (frame.plain && frame.depth != null && frame.depth.Count >= 40)
                {
                    var placing = Task.Run(() =>
                    {
                        foreach (var found in things) PlaceWithDepth(frame, found, things);
                        Untangle(things);
                    });
                    yield return Wait(placing);
                    if (placing.IsFaulted) foreach (var found in things) { found.placed = found.onSurface = false; found.note = "NOT PLACED: the placement failed | " + found.note; }
                }
                else
                {
                    foreach (var found in things)
                    {
                        Place(frame, found, things);
                        yield return null;   // one thing a frame
                    }
                    Untangle(things);
                }
                LastFound.AddRange(things);
                if (things.Count > 0)
                {
                    LastLocateNote = things[things.Count - 1].note;
                    LastBody.Clear();
                    LastBody.AddRange(things[things.Count - 1].body);
                }
                var lines = new List<string>();
                foreach (var found in LastFound)
                {
                    string line = found.seen.name + (found.byDepth ? " by depth" : " by desk plane") + (found.placed ? "" : " NOT PLACED") + " at " + found.basePoint.ToString("F3") +
                                  " width " + found.width.ToString("F3") + " height " + found.height.ToString("F3") + " | " + found.note + " | pose " + LastPoseNote;
                    Debug.Log("[Windups Eyes] " + line);
                    lines.Add(line);
                }
                foreach (var s in LastDropped) lines.Add(s.name + " dropped by the zoomed-in check");
                if (LastFound.Count > 0 && !LastFound.Any(f => f.seen.focus))
                    LastFound.OrderBy(f => f.placed ? 0 : 1).ThenBy(f => Centrality(f.seen)).First().seen.focus = true;
                // The record is written a moment later (so its frame times include the name tags going up),
                // on a worker thread.
                watch.phase = "after (name tags)";
                StartCoroutine(SaveSoon(watch, frame, question, json, confirm, first, LastDropped.ToList(), LastFound.ToList(), LastPoseNote, lines));
            }
            else watch.on = false;
            UseDepth(false);
            Looking = false;
            done(LastFound);
        }

        // How the frames went during a look (until its record is written, a moment after it): the longest
        // one, how many took longer than a 60 fps frame, and in which part of the look those fell.
        public sealed class FrameWatch
        {
            public bool on = true;
            public int frames, slow;
            public float worst;
            public string phase = "picture";
            public readonly Dictionary<string, (int count, float worst)> slowBy = new Dictionary<string, (int, float)>();
            public override string ToString() =>
                "frames " + frames + ", worst " + worst.ToString("F1") + " ms, over " + SlowFrameMs.ToString("F1") + " ms (60 fps): " + slow +
                (slowBy.Count > 0 ? " (" + string.Join(", ", slowBy.Select(x => x.Key + " x" + x.Value.count + " up to " + x.Value.worst.ToString("F0") + " ms")) + ")" : "");
        }

        public const float SlowFrameMs = 1000f / 60f + .5f;
        private FrameWatch lastWatch;
        public FrameWatch LastWatch => lastWatch;
        public float LastWorstFrameMs => lastWatch?.worst ?? 0f;
        public int LastSlowFrames => lastWatch?.slow ?? 0;

        private IEnumerator WatchFrames(FrameWatch watch)
        {
            yield return null;
            while (watch.on && lastWatch == watch)
            {
                yield return null;
                float ms = Time.unscaledDeltaTime * 1000f;
                watch.frames++;
                if (ms > watch.worst) watch.worst = ms;
                if (ms > SlowFrameMs)
                {
                    watch.slow++;
                    watch.slowBy.TryGetValue(watch.phase, out var was);
                    watch.slowBy[watch.phase] = (was.count + 1, Mathf.Max(was.worst, ms));
                }
            }
            watch.on = false;
        }

        private IEnumerator SaveSoon(FrameWatch watch, Frame frame, string question, string json, string confirm, List<Seen> first, List<Seen> dropped, List<Found> found, string poseNote, List<string> lines)
        {
            float until = Time.realtimeSinceStartup + .6f;
            while (Time.realtimeSinceStartup < until && watch.on) yield return null;
            watch.on = false;
            frame.timing = watch + " | depth " + (frame.depth?.Count ?? 0) + " points over " + frame.depthFrames + " frames at " + DepthBudgetMs.ToString("F1") + " ms a frame";
            Debug.Log("[Windups Eyes] " + frame.timing);
            _ = Task.Run(() =>
            {
                foreach (string line in lines) Remember(line);
                Remember(frame.timing);
                SaveLook(frame, question, json, confirm, first, dropped, found, poseNote);
            });
        }

        private IEnumerator Ask(string body, Action<string> done) => Ask(Encoding.UTF8.GetBytes(body), done);

        private IEnumerator Ask(byte[] body, Action<string> done)
        {
            if (!WindupsDirectCredentials.TryGetKey(out string key)) { LastError = "AI access is not configured in this test build."; done(null); yield break; }
            string json = null;
            using (var http = new UnityWebRequest(WindupsDirectMeeting.Endpoint, "POST"))
            {
                http.uploadHandler = new UploadHandlerRaw(body);
                http.downloadHandler = new DownloadHandlerBuffer();
                http.SetRequestHeader("Content-Type", "application/json");
                http.SetRequestHeader("Authorization", "Bearer " + key);
                http.timeout = 60;
                yield return http.SendWebRequest();
                if (http.result == UnityWebRequest.Result.Success)
                    try { json = MakerRoles.ReadOutput(http.downloadHandler.text); }
                    catch (Exception exception) { LastError = "I couldn't make sense of it."; Debug.LogWarning("[Windups Eyes] " + exception.Message); }
                else LastError = "Could not connect. Check the internet.";
            }
            done(json);
        }

        private static float Centrality(Seen s) => new Vector2((s.box[0] + s.box[2]) * .5f - .5f, (s.box[1] + s.box[3]) * .5f - .5f).magnitude;

        private const string VisionModel = "gpt-5.4";

        public static string VisionRequest(string question, byte[] jpeg)
        {
            var schema = new JObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["objects"] = new JObject { ["type"] = "array", ["items"] = new JObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false,
                        ["properties"] = new JObject
                        {
                            ["name"] = new JObject { ["type"] = "string" },
                            ["box"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" } },
                            ["colors"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                            ["surface"] = new JObject { ["type"] = "string" },
                            ["focus"] = new JObject { ["type"] = "boolean" }
                        },
                        ["required"] = new JArray("name", "box", "colors", "surface", "focus")
                    } }
                },
                ["required"] = new JArray("objects")
            };
            return new JObject
            {
                ["model"] = VisionModel, ["store"] = false, ["max_output_tokens"] = 3000,
                ["instructions"] = "This is a photo from the person's mixed reality headset camera, looking at their desk or room. List up to " + MaxObjects + " distinct physical objects that stand or lie on the desk or another surface near the person, are fully visible, and that you can identify for certain. Leave out: walls, the floor, the tabletop itself, furniture and appliances (chairs, tables, shelves, cupboards, ovens), anything shown on a screen, reflections, the person's hands and arms, anything cut off by the edge of the photo, and anything far away in the background. Never guess: if something is blurry, dark or could be several things, leave it out; listing fewer objects is better than one wrong one, and an empty list is fine. name = a short everyday name in plain English (max 3 words). box = [x0, y0, x1, y1] in 0-1 image coordinates, origin top-left, tight around the visible object only: its top edge at the object's highest visible point, its bottom edge where the object touches what it stands on (never its shadow, its reflection, the surface under it or what is behind it). colors = 1 to 3 main colours as #rrggbb. surface = material and texture in max 8 words. focus = true for the ONE object the person means (see their words); if their words don't name one, the object nearest the centre of the photo.",
                ["input"] = new JArray(new JObject
                {
                    ["role"] = "user",
                    ["content"] = new JArray(
                        new JObject { ["type"] = "input_text", ["text"] = "The person said: " + (question ?? "") },
                        new JObject { ["type"] = "input_image", ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg), ["detail"] = "high" })
                }),
                ["text"] = new JObject { ["format"] = new JObject { ["type"] = "json_schema", ["name"] = "windups_eyes", ["strict"] = true, ["schema"] = schema } }
            }.ToString(Formatting.None);
        }

        // ------------------------------------------------------------ the second, zoomed-in look

        [Serializable] public sealed class Check
        {
            public int id;
            public bool present;
            public string name;
            public float[] box;      // in the zoomed picture, in its ruler's units (0-10), origin top-left
        }

        // 2026-11-12: asked for a box in a 512 px zoom, the model was off by a tenth of the picture or more
        // (a mug's box half a mug to the left, a bottle's box half a bottle too low). Measured on the saved
        // look of the 8th try, the same model is within a few pixels to a twentieth of the picture when the
        // zoom is 1024 px and has a ruler drawn over it to read the edges off (overlap with the real box:
        // mug .30 -> .95, pill bottle .23 -> .70).
        public const int ZoomPixels = 1024, RulerLines = 10;

        // The part of the picture around a box: square in pixels, a bit more than twice the box, at least
        // a quarter of the picture's height. 0-1, origin top-left.
        public static Rect ZoomRect(Frame frame, float[] box)
        {
            float w = frame.size.x, h = frame.size.y;
            float bx0 = Mathf.Min(box[0], box[2]), bx1 = Mathf.Max(box[0], box[2]), by0 = Mathf.Min(box[1], box[3]), by1 = Mathf.Max(box[1], box[3]);
            float side = Mathf.Min(Mathf.Max(Mathf.Max((bx1 - bx0) * w, (by1 - by0) * h) * 2.2f, h * .25f), Mathf.Min(w, h));
            float x = Mathf.Clamp((bx0 + bx1) * .5f * w - side * .5f, 0f, w - side), y = Mathf.Clamp((by0 + by1) * .5f * h - side * .5f, 0f, h - side);
            return new Rect(x / w, y / h, side / w, side / h);
        }

        // That part as a JPEG of `pixels` x `pixels` (small things are enlarged for the model). `ruled`: with
        // the ruler the model reads the box off (Ruler).
        public static byte[] Zoom(Frame frame, Rect part, int pixels = ZoomPixels, bool ruled = false)
        {
            if (frame?.pixels == null) return null;
            int w = frame.size.x, h = frame.size.y;
            var source = frame.pixels;
            var big = new Color32[pixels * pixels];
            // `part` is in shares of the picture from its top-left; rows of both arrays run bottom first.
            float x0 = part.x * w, y0 = part.y * h, stepX = part.width * w / pixels, stepY = part.height * h / pixels;
            for (int y = 0; y < pixels; y++)
            {
                float fy = Mathf.Clamp(y0 + (y + .5f) * stepY - .5f, 0f, h - 1f);
                int ya = (int)fy, yb = Mathf.Min(ya + 1, h - 1);
                float ty = fy - ya;
                int rowA = (h - 1 - ya) * w, rowB = (h - 1 - yb) * w, o = (pixels - 1 - y) * pixels;
                for (int x = 0; x < pixels; x++)
                {
                    float fx = Mathf.Clamp(x0 + (x + .5f) * stepX - .5f, 0f, w - 1f);
                    int xa = (int)fx, xb = Mathf.Min(xa + 1, w - 1);
                    float tx = fx - xa;
                    Color32 a = source[rowA + xa], b = source[rowA + xb], c = source[rowB + xa], d = source[rowB + xb];
                    float wa = (1f - tx) * (1f - ty), wb = tx * (1f - ty), wc = (1f - tx) * ty, wd = tx * ty;
                    big[o + x] = new Color32((byte)(a.r * wa + b.r * wb + c.r * wc + d.r * wd + .5f), (byte)(a.g * wa + b.g * wb + c.g * wc + d.g * wd + .5f),
                        (byte)(a.b * wa + b.b * wb + c.b * wc + d.b * wd + .5f), 255);
                }
            }
            if (ruled) Ruler(big, pixels);
            return Jpeg(big, pixels, pixels, 85);
        }

        private static readonly string[] RulerDigits =
        {
            "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
            "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111"
        };

        // A ruler over a square picture (pixels as Texture2D gives them, bottom row first): thin yellow
        // lines at every tenth, numbered 1 to 9 along the top and down the left side.
        public static void Ruler(Color32[] pixels, int size)
        {
            var yellow = new Color32(255, 230, 0, 255);
            var black = new Color32(0, 0, 0, 255);
            void Set(int x, int y, Color32 c, float a)
            {
                if (x < 0 || y < 0 || x >= size || y >= size) return;
                int i = (size - 1 - y) * size + x;
                pixels[i] = a >= 1f ? c : Color32.Lerp(pixels[i], c, a);
            }
            for (int k = 1; k < RulerLines; k++)
            {
                int at = Mathf.RoundToInt(k * size / (float)RulerLines);
                for (int i = 0; i < size; i++) { Set(at, i, yellow, .55f); Set(i, at, yellow, .55f); }
            }
            int s = Mathf.Max(2, size / 256);
            void Digit(int d, int x0, int y0)
            {
                for (int dx = -s; dx < 4 * s; dx++)
                    for (int dy = -s; dy < 6 * s; dy++) Set(x0 + dx, y0 + dy, black, 1f);
                for (int r = 0; r < 5; r++)
                    for (int c = 0; c < 3; c++)
                        if (RulerDigits[d][r * 3 + c] == '1')
                            for (int dx = 0; dx < s; dx++)
                                for (int dy = 0; dy < s; dy++) Set(x0 + c * s + dx, y0 + r * s + dy, yellow, 1f);
            }
            for (int k = 1; k < RulerLines; k++)
            {
                int at = Mathf.RoundToInt(k * size / (float)RulerLines);
                Digit(k, at - 3 * s / 2, s + 1);
                Digit(k, s + 1, at - 5 * s / 2);
            }
        }

        public static string ConfirmRequest(Frame frame, List<Seen> first) =>
            ConfirmRequest(first, first.Select(s => Zoom(frame, ZoomRect(frame, s.firstBox ?? s.box), ZoomPixels, true) ?? new byte[0]).ToList());

        // `zooms`: each thing's zoomed, ruled picture (Zoom), in the order of `first`.
        public static string ConfirmRequest(List<Seen> first, List<byte[]> zooms)
        {
            var schema = new JObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["checks"] = new JObject { ["type"] = "array", ["items"] = new JObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false,
                        ["properties"] = new JObject
                        {
                            ["id"] = new JObject { ["type"] = "integer" },
                            ["present"] = new JObject { ["type"] = "boolean" },
                            ["name"] = new JObject { ["type"] = "string" },
                            ["box"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" } }
                        },
                        ["required"] = new JArray("id", "present", "name", "box")
                    } }
                },
                ["required"] = new JArray("checks")
            };
            var content = new JArray();
            var list = new StringBuilder();
            for (int i = 0; i < first.Count; i++) list.Append("Picture ").Append(i + 1).Append(" should show: ").Append(first[i].name).Append(". ");
            content.Add(new JObject { ["type"] = "input_text", ["text"] = list.ToString() });
            foreach (var jpeg in zooms)
            {
                content.Add(new JObject { ["type"] = "input_image", ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg), ["detail"] = "high" });
            }
            return new JObject
            {
                ["model"] = VisionModel, ["store"] = false, ["max_output_tokens"] = 2000,
                ["instructions"] = "Each picture is a zoomed-in part of a photo from a headset camera. For each picture you are told which object should be near its middle. Return one entry per picture, id = the picture's number. present = true only if that object is really there, clearly visible and mostly inside the picture; false if it is not there, if only an edge of it shows, if it is a reflection or something shown on a screen, or if it is a hand or an arm. name = the best short everyday name for it in plain English (max 3 words); correct the given name if it is wrong. A yellow ruler is drawn over each picture: its vertical lines are numbered 1 to 9 along the top, its horizontal lines 1 to 9 down the left side; the left and top edges are 0, the right and bottom edges are 10. box = [x0, y0, x1, y1] in these ruler units with one decimal (for example 3.4), tight around the whole visible object: read each edge off the nearest ruler lines. Its top edge is the object's highest visible point, its bottom edge where the object touches what it stands on (never its shadow, its reflection, the surface under it or what is behind it). When present is false, box = [0, 0, 0, 0].",
                ["input"] = new JArray(new JObject { ["role"] = "user", ["content"] = content }),
                ["text"] = new JObject { ["format"] = new JObject { ["type"] = "json_schema", ["name"] = "windups_eyes_check", ["strict"] = true, ["schema"] = schema } }
            }.ToString(Formatting.None);
        }

        // The first answer after the zoomed-in check: what is not there goes to `dropped`, names are
        // corrected, boxes come from the zoomed picture (mapped back to the whole one), and a thing listed
        // twice is kept once.
        public static List<Seen> Confirmed(Frame frame, List<Seen> first, string confirm, List<Seen> dropped)
        {
            List<Check> checks;
            try { checks = JObject.Parse(confirm)["checks"]?.ToObject<List<Check>>(); }
            catch (Exception exception) { Debug.LogWarning("[Windups Eyes] check: " + exception.Message); return first; }
            var kept = new List<Seen>();
            for (int i = 0; i < first.Count; i++)
            {
                var s = first[i];
                var c = checks?.FirstOrDefault(x => x != null && x.id == i + 1);
                if (c != null && !c.present) { dropped.Add(s); continue; }
                if (c != null)
                {
                    if (!string.IsNullOrWhiteSpace(c.name)) s.name = c.name.Trim();
                    // Ruler units (0-10); an answer wholly within 0-1 was given as shares of the picture instead.
                    float unit = c.box != null && c.box.Length == 4 && c.box.Max() <= 1f ? 1f : 1f / RulerLines;
                    if (c.box != null && c.box.Length == 4 && Mathf.Abs(c.box[2] - c.box[0]) * unit > .03f && Mathf.Abs(c.box[3] - c.box[1]) * unit > .03f)
                    {
                        var part = ZoomRect(frame, s.firstBox ?? s.box);
                        s.box = new[]
                        {
                            Mathf.Clamp01(part.x + Mathf.Clamp01(Mathf.Min(c.box[0], c.box[2]) * unit) * part.width), Mathf.Clamp01(part.y + Mathf.Clamp01(Mathf.Min(c.box[1], c.box[3]) * unit) * part.height),
                            Mathf.Clamp01(part.x + Mathf.Clamp01(Mathf.Max(c.box[0], c.box[2]) * unit) * part.width), Mathf.Clamp01(part.y + Mathf.Clamp01(Mathf.Max(c.box[1], c.box[3]) * unit) * part.height)
                        };
                    }
                }
                if (kept.Any(k => Overlap(k.box, s.box) > .6f)) { dropped.Add(s); continue; }
                kept.Add(s);
            }
            return kept;
        }

        // How much two boxes are the same (intersection over union).
        public static float Overlap(float[] a, float[] b)
        {
            float ax0 = Mathf.Min(a[0], a[2]), ax1 = Mathf.Max(a[0], a[2]), ay0 = Mathf.Min(a[1], a[3]), ay1 = Mathf.Max(a[1], a[3]);
            float bx0 = Mathf.Min(b[0], b[2]), bx1 = Mathf.Max(b[0], b[2]), by0 = Mathf.Min(b[1], b[3]), by1 = Mathf.Max(b[1], b[3]);
            float iw = Mathf.Min(ax1, bx1) - Mathf.Max(ax0, bx0), ih = Mathf.Min(ay1, by1) - Mathf.Max(ay0, by0);
            if (iw <= 0f || ih <= 0f) return 0f;
            float inter = iw * ih, union = (ax1 - ax0) * (ay1 - ay0) + (bx1 - bx0) * (by1 - by0) - inter;
            return union > 1e-9f ? inter / union : 0f;
        }

        // The points split into connected surfaces: neighbours closer than about two and a half sample
        // spacings (or three times the depth noise), 1 to 3.5 cm, so a jump in depth (the gap to whatever
        // stands behind) ends a surface. (Points are sorted into 3.5 cm cells, so a few thousand cost little.)
        private static float LastLink;
        private static List<List<DepthPoint>> Surfaces(List<DepthPoint> points, float noise)
        {
            int n = points.Count;
            const float cell = .035f;
            var cells = new Dictionary<Vector3Int, List<int>>();
            Vector3Int Cell(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
            for (int i = 0; i < n; i++)
            {
                var key = Cell(points[i].point);
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
                list.Add(i);
            }
            var around = new List<int>();
            void Around(int i)
            {
                around.Clear();
                var c = Cell(points[i].point);
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                            if (cells.TryGetValue(new Vector3Int(c.x + x, c.y + y, c.z + z), out var list)) around.AddRange(list);
            }
            // The usual spacing: how far the nearest neighbour is, for a couple of hundred of the points.
            // Measured within one set of rays: where the grid's rays and a thing's extra rays fall almost on
            // each other the nearest point is millimetres away, and a link made from that cut a mug's top
            // into rows; a link made wide enough for the mix joined the mug to what stood behind it. The
            // extra rays (about half the grid's spacing) set the link when they are there.
            var fine = new List<float>();
            var coarse = new List<float>();
            int extra = 0;
            for (int i = 0; i < n; i++) if (points[i].owner != 0) extra++;
            int stride = Mathf.Max(1, n / 300);
            for (int i = 0; i < n; i += stride)
            {
                bool own = points[i].owner != 0;
                float m = float.PositiveInfinity;
                Around(i);
                foreach (int j in around)
                    if (i != j && (points[j].owner != 0) == own)
                    {
                        float d = (points[i].point - points[j].point).sqrMagnitude;
                        if (d > 1e-8f && d < m) m = d;
                    }
                (own ? fine : coarse).Add(float.IsPositiveInfinity(m) ? cell : Mathf.Sqrt(m));
            }
            var nearest = fine.Count >= 8 && extra >= n * .3f ? fine : coarse.Count > 0 ? coarse : fine;
            nearest.Sort();
            float link = Mathf.Clamp(Mathf.Max((nearest.Count > 0 ? nearest[nearest.Count / 2] : .01f) * 2.5f, noise * 3f), .01f, cell);
            LastLink = link;
            var label = new int[n];
            var parts = new List<List<DepthPoint>>();
            var queue = new Queue<int>();
            for (int s0 = 0; s0 < n; s0++)
            {
                if (label[s0] != 0) continue;
                var part = new List<DepthPoint>();
                label[s0] = parts.Count + 1;
                queue.Enqueue(s0);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    part.Add(points[i]);
                    Around(i);
                    foreach (int j in around)
                        if (label[j] == 0 && (points[i].point - points[j].point).sqrMagnitude < link * link) { label[j] = label[s0]; queue.Enqueue(j); }
                }
                parts.Add(part);
            }
            return parts;
        }

        // A circle through 2D points that most of them lie on (within `on`, 8 mm or more with noisy depth): random triples propose circles
        // (a fixed seed, so it is repeatable), the one with the most points on it wins, then a least-squares
        // fit through those points. `kept` is the share of points on the circle. Whatever touches the
        // object (a monitor behind a mug, the desk edge) does not lie on the circle and is left out.
        private static bool FitCircle(List<Vector2> points, float on, out Vector2 centre, out float radius, out float kept)
        {
            centre = Vector2.zero;
            radius = 0f;
            kept = 0f;
            if (points.Count < 6) return false;
            var random = new System.Random(7);
            int best = 0;
            Vector2 bestCentre = Vector2.zero;
            float bestRadius = 0f;
            for (int k = 0; k < 200; k++)
            {
                Vector2 a = points[random.Next(points.Count)], b = points[random.Next(points.Count)], c = points[random.Next(points.Count)];
                if (!SolveCircle(new List<Vector2> { a, b, c }, out var cc, out float rr) || rr < .005f || rr > .25f) continue;
                int count = 0;
                foreach (var q in points) if (Mathf.Abs((q - cc).magnitude - rr) < on) count++;
                if (count > best) { best = count; bestCentre = cc; bestRadius = rr; }
            }
            if (best < 6) return false;
            var use = points.Where(q => Mathf.Abs((q - bestCentre).magnitude - bestRadius) < on).ToList();
            if (!SolveCircle(use, out centre, out radius)) { centre = bestCentre; radius = bestRadius; }
            var c2 = centre;
            float r2 = radius;
            kept = points.Count(q => Mathf.Abs((q - c2).magnitude - r2) < on) / (float)points.Count;
            return true;
        }

        // Algebraic circle fit: x^2 + y^2 + Dx + Ey + F = 0 in the least-squares sense (centred for precision).
        private static bool SolveCircle(List<Vector2> points, out Vector2 centre, out float radius)
        {
            centre = Vector2.zero;
            radius = 0f;
            Vector2 mean = Vector2.zero;
            foreach (var q in points) mean += q;
            mean /= points.Count;
            double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sz = 0, sxz = 0, syz = 0, n = points.Count;
            foreach (var q0 in points)
            {
                double x = q0.x - mean.x, y = q0.y - mean.y, z = x * x + y * y;
                sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y; sz += z; sxz += x * z; syz += y * z;
            }
            // [sxx sxy sx; sxy syy sy; sx sy n] [D E F] = -[sxz syz sz]
            double a11 = sxx, a12 = sxy, a13 = sx, a22 = syy, a23 = sy, a33 = n;
            double b1 = -sxz, b2 = -syz, b3 = -sz;
            double det = a11 * (a22 * a33 - a23 * a23) - a12 * (a12 * a33 - a23 * a13) + a13 * (a12 * a23 - a22 * a13);
            if (Math.Abs(det) < 1e-18) return false;
            double d = (b1 * (a22 * a33 - a23 * a23) - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a23 - a22 * b3)) / det;
            double e = (a11 * (b2 * a33 - a23 * b3) - b1 * (a12 * a33 - a23 * a13) + a13 * (a12 * b3 - b2 * a13)) / det;
            double f = (a11 * (a22 * b3 - b2 * a23) - a12 * (a12 * b3 - b2 * a13) + b1 * (a12 * a23 - a22 * a13)) / det;
            double cx = -d / 2, cy = -e / 2, rr = cx * cx + cy * cy - f;
            if (rr <= 0) return false;
            centre = new Vector2((float)cx + mean.x, (float)cy + mean.y);
            radius = (float)Math.Sqrt(rr);
            return true;
        }

        // The usual gap between neighbouring sample rows or columns (values closer than 3 mm are one).
        private static float SampleGap(List<float> sorted)
        {
            var gaps = new List<float>();
            for (int i = 1; i < sorted.Count; i++) if (sorted[i] - sorted[i - 1] > .003f) gaps.Add(sorted[i] - sorted[i - 1]);
            if (gaps.Count == 0) return 0f;
            gaps.Sort();
            return Mathf.Min(gaps[gaps.Count / 2], .03f);
        }

        // Finds the patch of the object's main colour near the model's box (a wider window, since the
        // box can be off) and returns its tight box, or null when the colour doesn't stand out.
        public static float[] Refine(Frame frame, Seen seen)
        {
            if (frame.pixels == null || seen.colors == null || seen.colors.Length == 0) return null;
            if (!TryColour(seen.colors[0], out var target)) return null;
            Color.RGBToHSV(target, out float th, out float ts, out float tv);
            bool vivid = ts > .28f && tv > .2f;
            // Black, white and grey things match their background too (black glasses took the monitor behind
            // them, a white box the wall): only a vivid colour can tighten the box.
            if (!vivid) return null;
            int w = frame.size.x, h = frame.size.y, step = Mathf.Max(1, w / 240);
            float bx0 = Mathf.Min(seen.box[0], seen.box[2]), bx1 = Mathf.Max(seen.box[0], seen.box[2]);
            float by0 = Mathf.Min(seen.box[1], seen.box[3]), by1 = Mathf.Max(seen.box[1], seen.box[3]);
            float margin = Mathf.Max(.015f, Mathf.Max(bx1 - bx0, by1 - by0) * .25f);
            int gx0 = Mathf.Clamp((int)((bx0 - margin) * w) / step, 0, w / step - 1), gx1 = Mathf.Clamp((int)((bx1 + margin) * w) / step, 0, w / step - 1);
            int gy0 = Mathf.Clamp((int)((by0 - margin) * h) / step, 0, h / step - 1), gy1 = Mathf.Clamp((int)((by1 + margin) * h) / step, 0, h / step - 1);
            int gw = gx1 - gx0 + 1, gh = gy1 - gy0 + 1;
            if (gw < 3 || gh < 3) return null;
            var pixels = frame.pixels;
            var mask = new bool[gw * gh];
            int hits = 0;
            for (int gy = 0; gy < gh; gy++)
                for (int gx = 0; gx < gw; gx++)
                {
                    int px = (gx0 + gx) * step, py = h - 1 - (gy0 + gy) * step; // box y is from the top
                    Color c = pixels[py * w + px];
                    Color.RGBToHSV(c, out float hh, out float ss, out float vv);
                    bool match = vivid
                        ? Mathf.Min(Mathf.Abs(hh - th), 1f - Mathf.Abs(hh - th)) < .05f && ss > ts * .55f && vv > .15f
                        : Mathf.Abs(vv - tv) < .12f && Mathf.Abs(ss - ts) < .15f;
                    if (match) { mask[gy * gw + gx] = true; hits++; }
                }
            if (hits < 6 || hits > gw * gh * .5f) return null;
            // The connected patch nearest the model's box centre.
            var label = new int[gw * gh];
            var stack = new Stack<int>();
            float cx = ((bx0 + bx1) * .5f * w / step) - gx0, cy = ((by0 + by1) * .5f * h / step) - gy0;
            float best = float.PositiveInfinity;
            int bestLabel = 0, next = 0;
            var bounds = new Dictionary<int, (int x0, int y0, int x1, int y1, int n)>();
            // The model's box in grid cells: the patch with most of its cells inside it wins.
            float mx0 = bx0 * w / step - gx0, mx1 = bx1 * w / step - gx0, my0 = by0 * h / step - gy0, my1 = by1 * h / step - gy0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i] || label[i] != 0) continue;
                next++;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, n = 0, inside = 0;
                stack.Push(i);
                label[i] = next;
                while (stack.Count > 0)
                {
                    int j = stack.Pop(), x = j % gw, y = j / gw;
                    n++;
                    if (x >= mx0 && x <= mx1 && y >= my0 && y <= my1) inside++;
                    minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x); minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
                    foreach (int k in new[] { j - 1, j + 1, j - gw, j + gw })
                    {
                        if (k < 0 || k >= mask.Length || !mask[k] || label[k] != 0) continue;
                        if ((k == j - 1 || k == j + 1) && k / gw != y) continue;
                        label[k] = next;
                        stack.Push(k);
                    }
                }
                bounds[next] = (minX, minY, maxX, maxY, n);
                if (n < 6 || inside < n * .6f) continue;
                float d = -inside;
                if (d < best) { best = d; bestLabel = next; }
            }
            if (bestLabel == 0) return null;
            var b = bounds[bestLabel];
            // It must agree with the model's box: its middle inside it, not much bigger than it.
            float pcx = (b.x0 + b.x1 + 1) * .5f, pcy = (b.y0 + b.y1 + 1) * .5f;
            float patchArea = (b.x1 - b.x0 + 1) * (float)(b.y1 - b.y0 + 1), boxArea = Mathf.Max(1f, (mx1 - mx0) * (my1 - my0));
            if (pcx < mx0 || pcx > mx1 || pcy < my0 || pcy > my1 || patchArea > boxArea * 1.4f || patchArea < boxArea * .08f) return null;
            // Second pass at full resolution inside the coarse patch: robust edges (2nd-98th percentile).
            int fx0 = Mathf.Max(0, (gx0 + b.x0 - 1) * step), fx1 = Mathf.Min(w - 1, (gx0 + b.x1 + 2) * step);
            int fy0 = Mathf.Max(0, (gy0 + b.y0 - 1) * step), fy1 = Mathf.Min(h - 1, (gy0 + b.y1 + 2) * step);
            var xs = new List<int>();
            var ys = new List<int>();
            for (int y = fy0; y <= fy1; y++)
                for (int x = fx0; x <= fx1; x++)
                {
                    Color c = pixels[(h - 1 - y) * w + x];
                    Color.RGBToHSV(c, out float hh, out float ss, out float vv);
                    bool match = vivid
                        ? Mathf.Min(Mathf.Abs(hh - th), 1f - Mathf.Abs(hh - th)) < .05f && ss > ts * .55f && vv > .15f
                        : Mathf.Abs(vv - tv) < .12f && Mathf.Abs(ss - ts) < .15f;
                    if (match) { xs.Add(x); ys.Add(y); }
                }
            if (xs.Count < 12)
                return new[] { (gx0 + b.x0) * step / (float)w, (gy0 + b.y0) * step / (float)h, (gx0 + b.x1 + 1) * step / (float)w, (gy0 + b.y1 + 1) * step / (float)h };
            xs.Sort();
            ys.Sort();
            int lo = xs.Count / 50, hi = xs.Count - 1 - xs.Count / 50;
            return new[] { xs[lo] / (float)w, ys[lo] / (float)h, (xs[hi] + 1) / (float)w, (ys[hi] + 1) / (float)h };
        }

        private static Vector3 OnPlane(Plane plane, Ray ray, Vector3 fallback) => plane.Raycast(ray, out float d) && d > 0f ? ray.GetPoint(d) : fallback;

        // F29: the colour at a viewport point (a small patch, the brightest and darkest fifth left out so
        // a glint or a shadow edge does not decide it). Null outside the picture.
        public static Color? ColourAt(Frame frame, Vector2 viewport, float radius = .012f)
        {
            if (frame?.pixels == null || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f) return null;
            int w = frame.size.x, h = frame.size.y;
            int r = Mathf.Max(2, Mathf.RoundToInt(radius * w));
            int cx = Mathf.Clamp(Mathf.RoundToInt(viewport.x * w), r, w - r - 1), cy = Mathf.Clamp(Mathf.RoundToInt(viewport.y * h), r, h - r - 1);
            var patch = new List<Color>((r * 2 + 1) * (r * 2 + 1));
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++) patch.Add(frame.pixels[y * w + x]);
            var pixels = patch.OrderBy(c => c.r * .3f + c.g * .59f + c.b * .11f).ToList();
            int cut = pixels.Count / 5;
            var middle = pixels.Skip(cut).Take(Mathf.Max(1, pixels.Count - cut * 2)).ToList();
            Color sum = Color.black;
            foreach (var c in middle) sum += c;
            var colour = sum / middle.Count;
            colour.a = 1f;
            return colour;
        }

        // F29: a rectangle of the picture between two viewport corners, as a JPEG (a texture sample).
        // F69 `trueColour`: the room's light taken out of the crop (RoomEyes.Light.cs), as a picked colour has it.
        public static byte[] CropViewport(Frame frame, Vector2 a, Vector2 b, bool trueColour = false)
        {
            float x0 = Mathf.Clamp01(Mathf.Min(a.x, b.x)), x1 = Mathf.Clamp01(Mathf.Max(a.x, b.x));
            float y0 = Mathf.Clamp01(1f - Mathf.Max(a.y, b.y)), y1 = Mathf.Clamp01(1f - Mathf.Min(a.y, b.y));
            if (x1 - x0 < .01f || y1 - y0 < .01f) return null;
            return Crop(frame, x0, y0, x1, y1, trueColour);
        }

        // Just the object (with a little margin), as a JPEG for colour and texture references.
        public static byte[] Crop(Frame frame, float x0, float y0, float x1, float y1, bool trueColour = false)
        {
            if (frame.pixels == null) return null;
            int w = frame.size.x, h = frame.size.y;
            float mx = (x1 - x0) * .08f, my = (y1 - y0) * .08f;
            int px = Mathf.Clamp(Mathf.FloorToInt((x0 - mx) * w), 0, w - 2), py = Mathf.Clamp(Mathf.FloorToInt((1f - y1 - my) * h), 0, h - 2);
            int pw = Mathf.Clamp(Mathf.CeilToInt((x1 - x0 + 2f * mx) * w), 2, w - px), ph = Mathf.Clamp(Mathf.CeilToInt((y1 - y0 + 2f * my) * h), 2, h - py);
            var crop = new Color32[pw * ph];
            for (int row = 0; row < ph; row++) Array.Copy(frame.pixels, (py + row) * w + px, crop, row * pw, pw);
            if (trueColour) Correct(LightOf(frame), crop);
            return Jpeg(crop, pw, ph, 85);
        }

        // What Spark saw and how it placed it, kept on the headset (maker_eyes_log.txt) for checking later.
        public static string LogPath => System.IO.Path.Combine(dataFolder ?? Application.persistentDataPath, "maker_eyes_log.txt");
        private static void Remember(string line)
        {
            try
            {
                if (System.IO.File.Exists(LogPath) && new System.IO.FileInfo(LogPath).Length > 200000) System.IO.File.Delete(LogPath);
                System.IO.File.AppendAllText(LogPath, System.DateTime.Now.ToString("MM-dd HH:mm:ss ") + line + "\n");
            }
            catch { }
        }

        private void OnDestroy()
        {
            if (LastFrame?.texture != null) Destroy(LastFrame.texture);
            if (depthHolder != null) Destroy(depthHolder);
        }
    }
}
