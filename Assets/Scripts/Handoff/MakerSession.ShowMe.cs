using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Windups.Handoff
{
    // F56 (2026-11-13): the picture Spark's frame takes when its button is pressed (MakerSession.ShowFrame.cs has
    // the frame itself). "Ji-jing~": the scan line runs over the opening and the scan sound repeats until the
    // picture is ready, then "ti-ding!", the opening flashes and shows what was taken for a moment.
    //  - The frame lying on a surface: as the circle did, depth says what stands in it (8 mm above the surface
    //    measured around it) and colour says what lies flat in it. A standing thing is cut out with its height; a
    //    flat one (a drawing on paper, a phone) is straightened to the frame's rectangle like a scanned page.
    //  - The frame in the air: what the eyes see through it. Rays from the eyes through its corners meet the surface
    //    behind it (a plane through the depth points seen through the opening); that quad of the camera picture is
    //    straightened. With no depth behind it (a black screen) the frame's own corners are used, which is right
    //    when the frame is held close to the thing.
    //  - The cutting and the JPEG run in Task.Run on Frame.pixels (nothing heavy on the main thread).
    //  - Nothing in a lying frame: Spark says so; pressing again within a few seconds takes it anyway.
    public sealed partial class MakerSession
    {
        public const float FrameScanMin = 1.2f;
        public int ShowLooks { get; private set; }
        public string ShowAnswer { get; private set; } = "";
        public bool ShowScanning => frameScan != null && frameScan.gameObject.activeSelf;
        public int ScanSounds { get; private set; }
        public int ScanDings { get; private set; }
        public byte[] LastShot { get; private set; }
        public Vector2Int LastShotSize { get; private set; }
        public string LastShotHow { get; private set; } = "";

        private bool capturingShow;
        private float nothingSeenAt = -100f, scanSoundAt;
        // F88 (Quest 2026-11-15: "my hand shows up in the scan - could it scan one second after I press, so I can take my
        // hand away?"): the picture is taken ScanDelay after the press (or the words); meanwhile the label says "Hands away!"
        // and the scan line and its sound start only when the picture is really taken. Lying, it still also waits for the
        // hands to leave (up to 2.5 s more).
        public static float ScanDelay = 1f;
        private float scanFrom;
        public bool ScanWaiting => capturingShow && Time.time < scanFrom;
        public float LastScanDelay { get; private set; }

        private static bool SeeWords(string p) =>
            p.Contains("can you see") || p.Contains("do you see") || p.Contains("did you see") || p.Contains("see it") || p.Contains("look at it") ||
            p.Contains("look at this") || p.Contains("check it") || p.Contains("take it") || p.Contains("snap it") || p.Contains("take a picture") || p.Contains("capture") ||
            p.Contains("보여") || p.Contains("봐줘") || p.Contains("봐 줘") || p.Contains("보이니") || p.Contains("찍어") || p.Contains("캡처") || p.Contains("캡쳐");

        // The button (a fingertip, a pinch, the ray, the mouse) or the words.
        public void PressFrameButton()
        {
            if (!FrameShown || frameFlying || capturingShow || Time.time - framePressedAt < .6f) return;
            framePressedAt = Time.time;
            FramePresses++;
            ScanHintDone = true; // F88: the "Touch to scan" tag has done its job
            ToySounds.Play(ToySounds.Cue.Press, frameButton.position, .5f, 1.4f);
            ToyFx.Puff(frameButton.position, .004f, 2);
            Heard();
            // Pressed again just after "I don't see anything": taken as it is.
            TakeShowPicture(Time.time - nothingSeenAt < 12f);
        }

        private void TakeShowPicture(bool asked)
        {
            if (capturingShow || Eyes == null || !FrameShown || frameFlying) return;
            StartCoroutine(ShowPicture(asked));
        }

        // Tests: something was put in the frame and the picture asked for (the Editor has no hands).
        public void SimulateShowPlaced() => TakeShowPicture(true);

        private sealed class Shot
        {
            public bool present, standingThing;
            public int above;
            public float top, level, colour;
            public readonly List<Vector3> standing = new List<Vector3>();
            public Vector2[] quad;          // bottom-left, bottom-right, top-right, top-left of the picture to cut out
            public Vector2 boxMin, boxMax;  // a standing thing: the box round the frame and the thing's height
            public string how = "";
        }

        private IEnumerator ShowPicture(bool asked)
        {
            capturingShow = true;
            looking = true;
            ShowLooks++;
            ShowAnswer = "";
            float pressed = Time.time;
            scanFrom = pressed + ScanDelay;
            scanSoundAt = 0f;
            LookAt(Crew.Spark, showFrame.position, 3f);
            Body(Crew.Spark)?.Play("Peek");
            // F88: a second for the pressing hand to go (in the air too: the hand used to be in the picture at .25 s).
            while (Time.time < scanFrom && FrameShown) yield return null;
            bool lying = FrameLying;
            // Lying: the picture once no hand is over the frame or between the eyes and it (a blurred hand went to the
            // picture model once). In the air a hand may hold the thing shown, so only the pressing finger leaves.
            if (lying) for (float until = Time.time + 2.5f; Time.time < until && HandsOverFrame(); ) { scanFrom = Time.time + .02f; yield return null; }
            scanFrom = Time.time;
            LastScanDelay = Time.time - pressed;
            float began = Time.time; // (the scan is seen for FrameScanMin from here)
            RoomEyes.Frame frame = null;
            yield return Eyes.Capture(f => frame = f, true);
            float wait = Time.time + 2f;
            while (frame != null && frame.depthPending && Time.time < wait) yield return null;
            Shot shot = frame != null && FrameShown ? (lying ? LyingShot(frame, asked) : AirShot(frame)) : null;
            Color32[] pixels = null;
            int width = 0, height = 0;
            byte[] jpeg = null;
            if (shot != null && (shot.quad != null || shot.standingThing))
            {
                var size = FrameSize;
                var task = Task.Run(() =>
                {
                    if (shot.standingThing) pixels = RoomEyes.CropPixels(frame, shot.boxMin, shot.boxMax, out width, out height);
                    else
                    {
                        const int longSide = 640;
                        width = size.x >= size.y ? longSide : Mathf.Max(64, Mathf.RoundToInt(longSide * size.x / size.y));
                        height = size.y > size.x ? longSide : Mathf.Max(64, Mathf.RoundToInt(longSide * size.y / size.x));
                        pixels = RoomEyes.Rectify(frame.pixels, frame.size.x, frame.size.y, shot.quad[0], shot.quad[1], shot.quad[2], shot.quad[3], width, height);
                    }
                    if (pixels != null) jpeg = RoomEyes.Jpeg(pixels, width, height, 88);
                });
                while (!task.IsCompleted) yield return null;
                if (task.IsFaulted) Debug.LogWarning("[Maker] frame picture: " + task.Exception?.GetBaseException().Message);
            }
            // The scan is seen and heard for a moment even when the picture came at once.
            while (Time.time - began < FrameScanMin) yield return null;
            looking = false;
            capturingShow = false;
            if (Collecting && !Begun) { CollectShot(frame, shot, jpeg, pixels, width, height); yield break; } // F78 airplane mode
            if (PaintingStep) { if (frame == null) ScanFailed(Eyes.LastError ?? "I can't see it right now. Try again in a moment?"); else MoodShot(jpeg, pixels, width, height); yield break; } // F87
            if (!InInputs && Current != Step.ForObject) yield break;
            if (frame == null)
            {
                ShowAnswer = "no picture";
                ScanFailed(Eyes.LastError ?? "I can't see it right now. Try again in a moment?");
                yield break;
            }
            if (shot == null || jpeg == null)
            {
                ShowAnswer = "frame not in view";
                ScanFailed("I can't see my frame from here. Look at it with me?");
                PulseFrame();
                yield break;
            }
            LastShot = jpeg;
            LastShotSize = new Vector2Int(width, height);
            LastShotHow = shot.how;
            Talk.Add("Spark looked in its frame (" + shot.how + "): " + (shot.present ? "something there" : "nothing there") +
                     (lying ? " (depth " + shot.above + " over " + (shot.level - ShowDeskY).ToString("F3") + ", colour " + shot.colour.ToString("F2") + ")" : ""));
            // F45: the thing it is for goes in first; later the same thing still standing there is not the shape.
            if (Current == Step.ForObject)
            {
                if (shot.present || asked) ScanDone(pixels, width, height);
                ForObjectSeen(shot.present, asked, jpeg, shot.standing, shot.level, shot.top, lying);
                yield break;
            }
            if (lying && ForObjectStillThere(shot.present, asked, shot.standing, shot.top)) yield break;
            // Asked to take it ("this", or pressed again), it is taken even when it hardly shows (a very flat thing).
            if (!shot.present && !asked)
            {
                ShowAnswer = "nothing seen";
                nothingSeenAt = Time.time;
                ScanFailed("Hmm, I don't see anything in my frame. Press again and I'll take it anyway.");
                PulseFrameButton();
                yield break;
            }
            ShowAnswer = shot.present ? "taken" : "taken (asked)";
            ShowCaptures++;
            ScanDone(pixels, width, height);
            FramePicture(jpeg, pixels, width, height, shot.standingThing ? "object" : lying ? "flat" : "seen through the frame");
        }

        // "Ti-ding!": the opening flashes and shows what was taken for a moment.
        private void ScanDone(Color32[] pixels, int width, int height)
        {
            ScanDings++;
            ToySounds.Play(ToySounds.Cue.Ding, showFrame.position, .7f, 1f);
            ToyFx.Ring(showFrame.position + FrameFront * .004f, Quaternion.LookRotation(showFrame.forward, showFrame.up), Mathf.Max(FrameSize.x, FrameSize.y) * .55f);
            if (pixels == null) return;
            if (previewTexture == null || previewTexture.width != width || previewTexture.height != height)
            {
                if (previewTexture != null) Destroy(previewTexture);
                previewTexture = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            }
            previewTexture.SetPixels32(pixels);
            previewTexture.Apply(false);
            previewImage.texture = previewTexture;
            shotPreview.gameObject.SetActive(true);
            previewUntil = Time.time + .9f;
        }

        private void ScanFailed(string line)
        {
            ToySounds.Play(ToySounds.Cue.Bonk, showFrame != null ? showFrame.position : Vector3.zero, .4f, 1.2f);
            Say(Crew.Spark, line);
        }

        // The scan line and its sound while the picture is being taken.
        private void ScanLate()
        {
            if (frameScan == null) return;
            bool scan = capturingShow && Time.time >= scanFrom; // F88: not while the hands still have time to go
            if (frameScan.gameObject.activeSelf != scan)
            {
                frameScan.gameObject.SetActive(scan);
                if (!scan) foreach (var corner in frameCorners) corner.localScale = Vector3.one;
            }
            if (!scan) return;
            float t = Mathf.PingPong(Time.time / .55f, 1f);
            frameScan.localPosition = new Vector3(0f, Mathf.Lerp(-FrameSize.y * .5f, FrameSize.y * .5f, Mathf.SmoothStep(0f, 1f, t)), -.003f);
            frameScan.localScale = new Vector3(FrameSize.x - FrameBar, .012f, 1f);
            // (The corners light up with it.)
            float glow = 1f + .25f * Mathf.Abs(Mathf.Sin(Time.time * 9f));
            foreach (var corner in frameCorners) corner.localScale = Vector3.one * glow;
            if (Time.time < scanSoundAt) return;
            scanSoundAt = Time.time + .5f;
            ScanSounds++;
            ToySounds.Play(ToySounds.Cue.Scan, showFrame.position, .45f, 1f);
        }

        // ------------------------------------------------------------ what to cut out of the camera picture

        private static bool InView(Vector2 v) => v.x > -.02f && v.x < 1.02f && v.y > -.02f && v.y < 1.02f;

        // The frame lies on a surface: what stands or lies in it.
        private Shot LyingShot(RoomEyes.Frame frame, bool asked)
        {
            var shot = new Shot { level = ShowDeskY, how = "lying" };
            float halfX = FrameSize.x * .5f, halfY = FrameSize.y * .5f;
            if (frame.depth != null)
            {
                // The surface's own level is measured by depth just around the frame (the room model's desk can be
                // off by a centimetre), so a thing 8 mm up counts and the desk's noise does not.
                var around = new List<float>();
                foreach (var d in frame.depth)
                {
                    Vector3 l = FrameLocal(d.point);
                    float out_ = Mathf.Max(Mathf.Abs(l.x) - halfX, Mathf.Abs(l.y) - halfY);
                    if (out_ > .01f && out_ < .07f && Mathf.Abs(d.point.y - ShowDeskY) < .05f) around.Add(d.point.y);
                }
                if (around.Count >= 5) { around.Sort(); shot.level = around[around.Count / 2]; }
                foreach (var d in frame.depth)
                {
                    Vector3 l = FrameLocal(d.point);
                    float h = d.point.y - shot.level;
                    if (Mathf.Abs(l.x) < halfX * .9f && Mathf.Abs(l.y) < halfY * .9f && h > .008f && h < .5f) { shot.above++; shot.top = Mathf.Max(shot.top, h); shot.standing.Add(d.point); }
                }
            }
            bool coloured = ColourStandsOut(frame, out shot.colour);
            shot.present = shot.above >= 3 || coloured || (frame.depth == null && asked);
            var corners = new Vector2[4];
            bool seen = true;
            for (int i = 0; i < 4; i++)
            {
                corners[i] = frame.toViewport(FrameCorner(i) + Vector3.up * .002f);
                seen &= InView(corners[i]);
            }
            if (shot.above >= 3 && shot.top > .015f)
            {
                // A thing standing in it: the frame and the thing's height, as one upright cut of the picture.
                Vector2 min = new Vector2(1f, 1f), max = Vector2.zero;
                float height = Mathf.Max(shot.top, .04f) + .02f;
                for (int i = 0; i < 4; i++)
                    foreach (var p in new[] { FrameCorner(i), FrameCorner(i) + Vector3.up * height })
                    {
                        var v = frame.toViewport(p);
                        if (v.x < -.5f) continue;
                        min = Vector2.Min(min, v);
                        max = Vector2.Max(max, v);
                    }
                min = Vector2.Max(min, Vector2.zero);
                max = Vector2.Min(max, Vector2.one);
                if (max.x - min.x > .02f && max.y - min.y > .02f) { shot.standingThing = true; shot.boxMin = min; shot.boxMax = max; shot.how = "lying, a thing standing " + (shot.top * 1000f).ToString("0") + " mm"; }
                return shot;
            }
            if (seen) { shot.quad = corners; shot.how = "lying, flat"; }
            return shot;
        }

        // The frame in the air: what the eyes see through it.
        private Shot AirShot(RoomEyes.Frame frame)
        {
            var shot = new Shot { present = true, how = "in the air" };
            Vector3 eye = frame.headPosition != Vector3.zero ? frame.headPosition : Head != null ? Head.position : (Vector3)frame.Eye;
            Vector3 normal = showFrame.forward, centre = showFrame.position;
            var targets = new Vector3[4];
            for (int i = 0; i < 4; i++) targets[i] = FrameCorner(i);
            if (frame.depth != null)
            {
                // The depth points seen through the opening, beyond the frame.
                Vector3 sum = Vector3.zero, normals = Vector3.zero;
                int count = 0;
                float halfX = FrameSize.x * .5f, halfY = FrameSize.y * .5f;
                foreach (var d in frame.depth)
                {
                    Vector3 dir = d.point - eye;
                    float along = Vector3.Dot(dir, normal);
                    if (Mathf.Abs(along) < 1e-4f) continue;
                    float t = Vector3.Dot(centre - eye, normal) / along;
                    if (t <= 0f || t >= .97f) continue; // not beyond the frame
                    Vector3 l = FrameLocal(eye + dir * t);
                    if (Mathf.Abs(l.x) > halfX || Mathf.Abs(l.y) > halfY) continue;
                    sum += d.point;
                    normals += d.normal;
                    count++;
                }
                if (count >= 6)
                {
                    Vector3 on = sum / count;
                    Vector3 n = normals.sqrMagnitude > .25f * count * count ? normals.normalized : normal;
                    if (Mathf.Abs(Vector3.Dot(n, (on - eye).normalized)) < .25f) n = normal; // seen almost edge-on: not a surface to trust
                    bool all = true;
                    var hit = new Vector3[4];
                    for (int i = 0; i < 4 && all; i++)
                    {
                        Vector3 dir = targets[i] - eye;
                        float den = Vector3.Dot(dir, n);
                        float t = Mathf.Abs(den) > 1e-4f ? Vector3.Dot(on - eye, n) / den : -1f;
                        all = t >= 1f && t < 40f;
                        hit[i] = eye + dir * t;
                    }
                    if (all) { targets = hit; shot.how = "in the air, " + count + " depth points " + Vector3.Distance(on, centre).ToString("F2") + " m behind"; }
                }
            }
            var quad = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                quad[i] = frame.toViewport(targets[i]);
                if (!InView(quad[i])) return shot;
            }
            shot.quad = quad;
            return shot;
        }

        // Flat things (a card, a drawing) hardly stand up from the desk: something there is also told by its colour
        // inside the frame differing from the surface just around it.
        private bool ColourStandsOut(RoomEyes.Frame frame, out float difference)
        {
            difference = 0f;
            if (frame?.texture == null || frame.toViewport == null) return false;
            Color inside = Color.clear, around = Color.clear;
            int nIn = 0, nOut = 0;
            float halfX = FrameSize.x * .5f, halfY = FrameSize.y * .5f;
            for (int ix = -2; ix <= 2; ix++)
                for (int iy = -2; iy <= 2; iy++)
                {
                    var v = frame.toViewport(showFrame.TransformPoint(new Vector3(ix * halfX * .3f, iy * halfY * .3f, -.003f)));
                    if (v.x < 0f || v.x > 1f || v.y < 0f || v.y > 1f) continue;
                    inside += frame.texture.GetPixelBilinear(v.x, v.y);
                    nIn++;
                }
            for (int i = 0; i < 24; i++)
            {
                float a = i * Mathf.PI / 12f;
                // Round the frame, 3 cm outside its bars.
                float cx = Mathf.Cos(a), cy = Mathf.Sin(a), k = 1f / Mathf.Max(Mathf.Abs(cx) / (halfX + .03f), Mathf.Abs(cy) / (halfY + .03f));
                var o = frame.toViewport(showFrame.TransformPoint(new Vector3(cx * k, cy * k, -.002f)));
                if (o.x < 0f || o.x > 1f || o.y < 0f || o.y > 1f) continue;
                around += frame.texture.GetPixelBilinear(o.x, o.y);
                nOut++;
            }
            if (nIn < 10 || nOut < 8) return false;
            inside /= nIn;
            around /= nOut;
            difference = Mathf.Abs(inside.r - around.r) + Mathf.Abs(inside.g - around.g) + Mathf.Abs(inside.b - around.b);
            return difference > .16f;
        }
    }
}
