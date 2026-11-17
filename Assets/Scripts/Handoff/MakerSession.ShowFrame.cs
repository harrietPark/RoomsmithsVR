using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    // F56 (2026-11-13, the user: "change the ring we put things in to a square frame; let us grab and release it,
    // change its size, put a thing or a picture in it, or hold it up to a screen; a button on the frame takes the
    // picture"): Spark's frame replaces the show-me circle everywhere the circle was used (the thing the make is
    // for, the shape, the old colour step).
    //  - Spark brings it: it flies out from Spark onto a free spot of the working desk, lying flat, its button edge
    //    away from the person. When the step is over it flies back to Spark.
    //  - Only its rim grabs (near pinch, far pinch with the hand's ray, the Editor mouse), so hands putting things
    //    inside never move it. Let go within reach of a surface and roughly flat, it lies down on it; anywhere else
    //    it stays exactly where and how it was let go, in the air (a viewfinder for a screen or anything held up).
    //  - F60b (2026-11-13, after the Quest try: "resizing and grabbing the frame were too hard"): one hand anywhere on
    //    the rim, corners included, carries it (the corners used to resize it whenever it was picked up there);
    //    two hands on the rim stretch it like a photo (the hands' spread across sets the width, up and down the
    //    height); the mint handle at the lower right resizes it with one hand, keeping where it was taken (no jump).
    //    6-45 cm a side, any proportions; while it changes, the label shows the size. "Make the frame bigger /
    //    smaller", "phone size", "laptop size", "paper size", "square frame" by voice.
    //  - Easier to take: a thicker rim, taken up to 3.5 cm outside it and 1.5 cm inside (the opening stays free for
    //    the hands putting things in), the rim swells while a free hand is within reach, and the ray has more slack.
    //  - Carried in the air it always faces the person upright (the hand only moves it); over a surface it lies flat
    //    and settles there when let go.
    //  - The red button on its top edge takes the picture: a fingertip on it, a pinch on it, the hand's ray, the
    //    Editor mouse, or the words ("this", "take it"). Nothing is taken by itself any more: after a hand went in
    //    and out of a lying frame the button only bounces. The picture itself is MakerSession.ShowMe.cs.
    public sealed partial class MakerSession
    {
        public const float FrameMinSide = .06f, FrameMaxSide = .45f, FrameBar = .014f, FrameCornerLength = .03f,
            FrameHandleReach = .03f, FrameLieAbove = .07f;
        // F66 (the user: "the capture tool is still hard to take hold of; I want to grab and stretch it freely"): the band
        // that takes a pinch is 6 cm outside and 3 cm inside the outline (it was 3.5 / 1.5), up to FrameGripDepth off its
        // plane (was 4 cm); the middle of the opening stays free for the things put in it. With one hand on it, the other
        // hand pinching anywhere within FrameSecondReach of it stretches it (it had to be on the rim within 2 cm).
        public const float FrameGripOut = .06f, FrameGripIn = .03f, FrameGripDepth = .06f, FrameSecondReach = .1f;
        public static readonly Vector2 FrameDefault = new Vector2(.18f, .14f);
        // F73 (2026-11-14, the user after a Quest try: "the capture tool is hard to take; stretching is fine but grab and
        // release are not free"; they chose hold to carry). A hand carries it only while it pinches (no F67 grab &
        // release: a short pinch used to leave it stuck to the open hand, a long one dropped it - the person could not
        // tell which). It follows the hand rigidly: where and how it was taken stays with the fingers and the wrist, so it
        // tilts and turns with the hand (it used to turn itself to face the person and slid away from the fingers as it
        // turned, a step behind). While carried it is never flipped by itself (within 7 cm of a desk it used to lie down
        // and stand up again); a mint outline on the desk shows where it will lie if let go there, and let go it settles
        // flat in FrameSettleTime. Fingers open for under FrameDropGrace (hand tracking blinking) do not drop it, and it
        // stays still meanwhile. Taken from afar it flies to the hand (FrameFlyIn) and is then carried the same way.
        // Standing in the air, a pinch anywhere in its opening takes it too (lying, the opening stays free for things put
        // in it). The Editor mouse carries it as before (it cannot turn).
        public static float FrameDropGrace = .18f, FrameFlyIn = .22f, FrameSettleTime = .14f, FrameLieTilt = 35f, FrameOpeningDepth = .035f;
        public int FrameDrops { get; private set; }
        public bool FrameLandingShown => frameLanding != null && frameLanding.gameObject.activeSelf;
        public string LastFrameRelease { get; private set; } = "";
        private Vector3 frameHoldOffset;
        private Quaternion frameHoldTurn = Quaternion.identity, frameHoldHand = Quaternion.identity;
        private float frameOpenSince = -1f, frameFlyStart = -1f, frameHeldSince;
        private Pose frameFlyFrom;
        private Transform frameLanding;
        private readonly Transform[] landingBars = new Transform[4];
        private int frameSettleRun;

        public Vector2 FrameSize { get; private set; } = FrameDefault;
        public bool FrameShown => showFrame != null && showFrame.gameObject.activeSelf;
        public bool FrameLying { get; private set; }
        public bool FrameCarried => frameMode == FrameMode.Carry;
        public bool FrameResizing => frameMode == FrameMode.Resize;
        public bool FrameStretching => frameMode == FrameMode.Stretch;
        public bool FrameBusy => frameMode != FrameMode.None;
        public bool FrameHover => frameHover;
        public Transform FrameHandleView => frameHandle;
        public int FrameStretches { get; private set; }
        public Transform FrameView => showFrame;
        public Transform FrameButtonView => frameButton;
        public int FramePresses { get; private set; }
        public int FrameGrabs { get; private set; }
        public int FrameResizes { get; private set; }
        public int FrameButtonPulses { get; private set; }
        // (The names the circle had, kept for what reads them: the frame's middle and whether it is up.)
        public bool ShowCircleShown => FrameShown && !(frameFlying && !frameArriving); // (not once it is on its way back to Spark)
        public bool HandsOverCircle() => HandsOverFrame();
        public Vector3 ShowCircleCentre => showFrame == null ? Vector3.zero : frameArriving ? frameRestAt : showFrame.position; // (on its way out from Spark: where it will lie)
        public float ShowDeskY { get; private set; }
        // The room it takes on a surface, as a circle: the mean of its half sides (8.5 cm for the default frame, what
        // the circle took; its corners reach a little further). With the half diagonal, or even half the longer
        // side, the default frame found no free spot beside the ring on a small desk: it lay 3 cm into the ring,
        // or went to the far side of the desk onto a teammate's seat.
        public float ShowFootRadius => (FrameSize.x + FrameSize.y) * .25f + .005f;

        private enum FrameMode { None, Carry, Resize, Stretch }
        private FrameMode frameMode;
        private Transform showFrame, frameBody, showLabel, frameButton, frameDome, frameScan, shotPreview, frameHandle;
        private readonly Transform[] frameBars = new Transform[4], frameCorners = new Transform[4];
        private TextMeshProUGUI showText;
        private RawImage previewImage;
        private Texture2D previewTexture;
        private Hand frameHand, frameHand2;
        private bool frameHover;
        private float frameSwell = 1f;
        private Vector3 frameHandleOffset, stretchA0, stretchB0, stretchMid0, stretchCentre0;
        private Vector2 stretchSize0;
        private bool frameMouse, frameSim, frameFlying, frameArriving, handInFrame, handWasInFrame, frameHintSaid;
        private Vector3 frameRestAt;
        private int frameFlight;
        private float frameFar = -1f, handInSince = -1f, handLeftAt = -1f, framePulseAt = -10f, buttonPulseAt = -10f, framePressedAt = -10f, previewUntil;
        private Vector3 frameGrabLocal, frameAnchor;
        private Quaternion frameGrabTurn = Quaternion.identity;
        private Plane frameMousePlane;
        private int frameCorner = -1;
        private Pose frameSimPose;
        private Vector3 frameSimPoint;
        private readonly FingerPress framePress = new FingerPress(); // F60

        private static readonly int[] CornerX = { -1, 1, 1, -1 }, CornerY = { -1, -1, 1, 1 }; // bottom-left, bottom-right, top-right, top-left

        // A corner of the opening in the world (0 bottom-left, 1 bottom-right, 2 top-right, 3 top-left; "top" is the
        // button's edge, "bottom" the edge nearest the person when it lies on the desk).
        public Vector3 FrameCorner(int i) => showFrame.TransformPoint(new Vector3(CornerX[i] * FrameSize.x * .5f, CornerY[i] * FrameSize.y * .5f, 0f));
        // Out of its front (toward the person; up when it lies on the desk).
        public Vector3 FrameFront => showFrame != null ? -showFrame.forward : Vector3.up;

        // ------------------------------------------------------------ building it

        private void BuildShowFrame()
        {
            if (showFrame != null) return;
            showFrame = new GameObject("Show Me Frame").transform;
            showFrame.SetParent(transform, false);
            frameBody = new GameObject("Body").transform;
            frameBody.SetParent(showFrame, false);
            // F73 (the user's pick: draft C3 of Docs/Design/Concepts/Windups_NextPlan_2026-11-14/l2_CaptureFrameC_Textures.png):
            // Spark's viewfinder - four chunky L corners of green-painted wood (the grain shows through the paint, the
            // edges are bare wood), a thin dashed line between them, a green pill with the red shutter on its top edge and
            // a round green knob with three dots off the lower right corner for the size. One material (the crew's toy
            // shading, Resources/ToyCharacterProps) with one small grain texture made here once; the meshes are made
            // only when a size bucket is new (StickerMesh caches them by millimetre).
            var wood = FrameWood();
            for (int i = 0; i < 4; i++)
            {
                frameBars[i] = WoodPart("Edge", FrameDashes(.1f), frameBody, wood);
                if (i >= 2) frameBars[i].localRotation = Quaternion.Euler(0f, 0f, 90f);
            }
            for (int i = 0; i < 4; i++)
            {
                var corner = new GameObject("Corner " + i).transform;
                corner.SetParent(frameBody, false);
                WoodPart("Across", WoodArm(.04f), corner, wood);
                var along = WoodPart("Along", WoodArm(.04f), corner, wood);
                along.localRotation = Quaternion.Euler(0f, 0f, 90f);
                frameCorners[i] = corner;
            }
            frameArmMm = -1;
            // F60b: the one resize handle off the lower right corner: a round green knob with three white dots.
            frameHandle = new GameObject("Resize Handle").transform;
            frameHandle.SetParent(frameBody, false);
            // (a short rounded box, painted on both faces: a disc showed its bare wood from the back when held up)
            WoodPart("Knob", StickerMesh("frameKnobWood", () => WithGrainUv(ToyMeshes.RoundedBox(new Vector3(.021f, .021f, .009f), .0095f, PaintGreen, BareWood, BareWoodTop, PaintGreen, "FrameKnob"))), frameHandle, wood).localPosition = new Vector3(0f, 0f, -FrameLift);
            var dot = StickerMesh("frameKnobDot", () => ToyMeshes.Disc(.0017f, .0012f, DotWhite, DotWhite, "FrameKnobDot"));
            for (int i = -1; i <= 1; i++)
            {
                var d = WoodPart("Dot", dot, frameHandle, wood);
                d.localPosition = new Vector3(i * .0042f, -i * .0042f, -.0052f - FrameLift);
            }
            // The button: a green-painted pill on the top edge with a red dome (a toy camera's shutter).
            frameButton = new GameObject("Button").transform;
            frameButton.SetParent(frameBody, false);
            WoodPart("Pill", StickerMesh("framePillWood", () => WithGrainUv(ToyMeshes.RoundedBox(new Vector3(.046f, .022f, .011f), .0095f, PaintGreen, BareWood, BareWoodTop, PaintGreen, "FramePill"))), frameButton, wood).localPosition = new Vector3(0f, 0f, -.003f);
            Color red = new Color(.96f, .33f, .3f);
            frameDome = WoodPart("Dome", StickerMesh("frameDome", () => ToyMeshes.Disc(.0095f, .006f, red, Color.Lerp(red, Color.black, .28f), "FrameDome")), frameButton, wood);
            frameDome.localPosition = new Vector3(0f, 0f, -.0098f);
            // The scan line that runs over the opening while the picture is taken.
            Color light = new Color(.86f, 1f, .92f);
            frameScan = Part("Scan", StickerMesh("frameScan", () => ToyMeshes.RoundedSlab(1f, 1f, .3f, .0015f, light, light, light, "FrameScan")));
            frameScan.SetParent(frameBody, false);
            frameScan.gameObject.SetActive(false);
            // What was just taken, shown in the opening for a moment.
            var canvas = new GameObject("Preview", typeof(RectTransform), typeof(Canvas));
            shotPreview = canvas.transform;
            shotPreview.SetParent(frameBody, false);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            previewImage = new GameObject("Image", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            previewImage.rectTransform.SetParent(shotPreview, false);
            previewImage.rectTransform.anchorMin = Vector2.zero;
            previewImage.rectTransform.anchorMax = Vector2.one;
            previewImage.rectTransform.offsetMin = previewImage.rectTransform.offsetMax = Vector2.zero;
            previewImage.raycastTarget = false;
            shotPreview.gameObject.SetActive(false);
            var label = PaperTag("Show Me Label", .08f, .02f, new Color(.92f, 1f, .94f), out var text, 9f, false); // F77: a paper label, not a pill
            text.text = "Show me";
            showText = text;
            label.SetParent(showFrame, false);
            showLabel = label;
            LayoutFrame();
            showFrame.gameObject.SetActive(false);
        }

        // The parts for the opening's size now.
        private void LayoutFrame()
        {
            if (showFrame == null) return;
            float w = FrameSize.x, h = FrameSize.y;
            // F73: the corners' arms grow with the smaller side (a third of it, 2.2-5 cm), the dashes fill between them.
            float arm = Mathf.Clamp(Mathf.Min(w, h) * .3f, .022f, .05f);
            int armMm = Mathf.RoundToInt(arm * 1000f);
            if (armMm != frameArmMm)
            {
                frameArmMm = armMm;
                arm = armMm * .001f;
                for (int i = 0; i < 4; i++)
                {
                    var across = frameCorners[i].GetChild(0);
                    var along = frameCorners[i].GetChild(1);
                    across.GetComponent<MeshFilter>().sharedMesh = WoodArm(arm);
                    // (the second arm starts inside the first by its rounding, so the L reads as one joined piece)
                    along.GetComponent<MeshFilter>().sharedMesh = WoodArm(arm - FrameArmWidth + FrameArmRound, true);
                    across.localPosition = new Vector3(-CornerX[i] * (arm * .5f - FrameArmWidth * .5f), 0f, -FrameLift);
                    along.localPosition = new Vector3(0f, -CornerY[i] * (FrameArmWidth * .5f - FrameArmRound + (arm - FrameArmWidth + FrameArmRound) * .5f), -FrameLift);
                }
            }
            arm = frameArmMm * .001f;
            frameBars[0].localPosition = new Vector3(0f, -h * .5f, .0005f);
            frameBars[1].localPosition = new Vector3(0f, h * .5f, .0005f);
            frameBars[2].localPosition = new Vector3(-w * .5f, 0f, .0005f);
            frameBars[3].localPosition = new Vector3(w * .5f, 0f, .0005f);
            SetDashes(frameBars[0], w - 2f * arm);
            SetDashes(frameBars[1], w - 2f * arm);
            SetDashes(frameBars[2], h - 2f * arm);
            SetDashes(frameBars[3], h - 2f * arm);
            for (int i = 0; i < 4; i++)
            {
                frameCorners[i].localPosition = new Vector3(CornerX[i] * w * .5f, CornerY[i] * h * .5f, 0f);
                frameCorners[i].localScale = Vector3.one * Mathf.Lerp(1f, 1.15f, frameSwell - 1f); // F60b: swells while a free hand is within reach
            }
            if (frameHandle != null) frameHandle.localPosition = HandleLocal();
            frameButton.localPosition = new Vector3(0f, h * .5f + .021f, 0f);
            var rect = (RectTransform)shotPreview;
            rect.sizeDelta = new Vector2((w - FrameBar) * 1000f, (h - FrameBar) * 1000f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, 0f, -.0015f);
            showLabel.localPosition = new Vector3(0f, -h * .5f - .02f, -.012f);
        }

        // ------------------------------------------------------------ F73: the painted wood (look C3)

        public const float FrameArmWidth = .015f, FrameArmDepth = .009f, FrameArmRound = .0035f, FrameLift = .002f,
            FrameDashLength = .006f, FrameDashGap = .005f, FrameDashWidth = .0024f;
        private static readonly Color PaintGreen = new Color(.4f, .77f, .53f), BareWood = new Color(.82f, .62f, .4f),
            BareWoodTop = new Color(.88f, .7f, .48f), DotWhite = new Color(.97f, .96f, .92f), DashCream = new Color(.96f, .94f, .88f);
        private Material frameWood;
        private int frameArmMm = -1;
        private readonly int[] frameDashMm = { -1, -1, -1, -1 };

        // The crew's toy shading (vertex colours) with a pale wood grain multiplied in.
        private Material FrameWood()
        {
            if (frameWood != null) return frameWood;
            var template = Resources.Load<Material>("ToyCharacterProps");
            frameWood = new Material(template != null ? template : material) { name = "Frame Painted Wood" };
            var grain = GrainTexture();
            owned.Add(grain);
            owned.Add(frameWood);
            frameWood.SetTexture("_BaseMap", grain);
            if (frameWood.HasProperty("_VertexColor")) frameWood.SetFloat("_VertexColor", 1f);
            return frameWood;
        }

        private Transform WoodPart(string name, Mesh mesh, Transform parent, Material look)
        {
            var part = Part(name, mesh);
            part.SetParent(parent, false);
            var r = part.GetComponent<MeshRenderer>();
            r.sharedMaterial = look;
            r.receiveShadows = false;
            return part;
        }

        // One arm of a corner, `length` long along its x (made once per millimetre and kept). The second arm of a corner
        // (`inner`) is a hair thinner: its rounded end tucks into the first arm with no faces on the same plane.
        private Mesh WoodArm(float length, bool inner = false)
        {
            int mm = Mathf.Max(4, Mathf.RoundToInt(length * 1000f));
            return StickerMesh((inner ? "frameWoodArmIn" : "frameWoodArm") + mm, () => WithGrainUv(ToyMeshes.RoundedBox(
                new Vector3(mm * .001f, FrameArmWidth - (inner ? .0008f : 0f), FrameArmDepth - (inner ? .0008f : 0f)), FrameArmRound,
                PaintGreen, BareWood, BareWoodTop, PaintGreen, "FrameWoodArm")));
        }

        // The grain runs along a part's x: u along it, v across it and through its thickness (no wrap within a part).
        private static Mesh WithGrainUv(Mesh mesh)
        {
            var v = mesh.vertices;
            var uv = new Vector2[v.Length];
            for (int i = 0; i < v.Length; i++) uv[i] = new Vector2(v[i].x / .07f + .5f, (v[i].y + v[i].z) / .04f + .5f);
            mesh.uv = uv;
            return mesh;
        }

        // A pale grain to multiply the paint and the bare wood with: streaks along u, bent a little, broken by noise.
        // 64 x 128 texels, made once when the frame is first built.
        private static Texture2D GrainTexture()
        {
            const int W = 64, H = 128;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = "Frame Grain", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, anisoLevel = 2 };
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = x / (float)W, v = y / (float)H;
                    float bend = .3f * Mathf.Sin((u + .13f) * Mathf.PI * 2f) + .25f * (Mathf.PerlinNoise(u * 3f + 7f, v * 2f) - .5f);
                    // (a few soft lines across a part, fine fibres along it; the first try read as stripes)
                    float lines = Mathf.Sin((v * 4f + bend) * Mathf.PI * 2f);
                    float fine = Mathf.PerlinNoise(u * 6f + 3f, v * 70f) - .5f;
                    float g = Mathf.Clamp(.94f + .04f * lines + .075f * fine, .84f, 1f);
                    px[y * W + x] = new Color32((byte)(g * 255f), (byte)(g * 249f), (byte)(g * 242f), 255);
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        // The dashed line between two corners, `length` long along x and centred (two-faced quads; none if too short).
        private Mesh FrameDashes(float length)
        {
            int mm = Mathf.Max(0, Mathf.RoundToInt(length * 1000f));
            return StickerMesh("frameDash" + mm, () =>
            {
                float total = mm * .001f, step = FrameDashLength + FrameDashGap;
                int count = Mathf.Max(0, Mathf.FloorToInt((total + FrameDashGap) / step));
                var verts = new List<Vector3>(count * 8);
                var cols = new List<Color>(count * 8);
                var norms = new List<Vector3>(count * 8);
                var uvs = new List<Vector2>(count * 8);
                var tris = new List<int>(count * 12);
                float start = -(count * step - FrameDashGap) * .5f, hw = FrameDashWidth * .5f;
                for (int i = 0; i < count; i++)
                {
                    float a = start + i * step, b = a + FrameDashLength;
                    for (int side = 0; side < 2; side++)
                    {
                        int o = verts.Count;
                        float z = side == 0 ? -.0002f : .0002f;
                        verts.Add(new Vector3(a, -hw, z)); verts.Add(new Vector3(b, -hw, z)); verts.Add(new Vector3(b, hw, z)); verts.Add(new Vector3(a, hw, z));
                        for (int k = 0; k < 4; k++) { cols.Add(DashCream); norms.Add(side == 0 ? Vector3.back : Vector3.forward); uvs.Add(new Vector2(.5f, .5f)); }
                        if (side == 0) tris.AddRange(new[] { o, o + 2, o + 1, o, o + 3, o + 2 });
                        else tris.AddRange(new[] { o, o + 1, o + 2, o, o + 2, o + 3 });
                    }
                }
                var mesh = new Mesh { name = "FrameDashes" };
                mesh.SetVertices(verts);
                mesh.SetNormals(norms);
                mesh.SetColors(cols);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();
                return mesh;
            });
        }

        private void SetDashes(Transform bar, float length)
        {
            int index = System.Array.IndexOf(frameBars, bar);
            int mm = Mathf.Max(0, Mathf.RoundToInt(length * 1000f));
            if (index < 0 || frameDashMm[index] == mm) return;
            frameDashMm[index] = mm;
            bar.GetComponent<MeshFilter>().sharedMesh = FrameDashes(length);
        }
        // ------------------------------------------------------------ putting it out, taking it back

        // On the working desk in front of the person, beside the topic ring (never on it, the mic or the crew).
        private void PlaceShowFrame()
        {
            BuildShowFrame();
            float radius = ShowFootRadius;
            Vector3 front = FrontPoint(.4f, -.35f, out Vector3 side);
            var busy = Occupied();
            // (F51: Occupied() lists the frame itself while it is up; a new place is not kept away from the old one.)
            if (FrameShown) busy.RemoveAll(b => Vector3.Distance(b.at, ShowCircleCentre) < .001f);
            // (The ring is asked for where it is or will be: placed before the ring showed, the frame lay 3 cm into it.)
            if (arrival != null && arrival.EnsureRing(out var ringCentre, out float ringRadius)) busy.Add((ringCentre, ringRadius + .03f));
            // What cannot step aside (things made, real things, notes, the ring) ...
            var stays = new List<(Vector3 at, float radius)>(busy);
            // ... and what can: the crew (they walk out of it) and the mic (it moves when crowded).
            foreach (var c in cast.Values) if (c != null) busy.Add((c.transform.position, .07f));
            if (arrival != null && !arrival.MicHeld && arrival.MicRest != Vector3.zero) busy.Add((arrival.MicRest - Vector3.up * WindupsArrival.MicBase, .05f));
            Vector3 best = Vector3.zero;
            bool found = false;
            Vector3 ahead = Vector3.ProjectOnPlane(Head != null ? Head.forward : Vector3.forward, Vector3.up).normalized;
            // On the working desk (never the floor while the person works at a desk); never on the ring, the mic or
            // a teammate. The floor only when there is no desk at all.
            bool desk = WorkSurface != null;
            foreach (bool floorToo in desk ? new[] { false } : new[] { false, true })
            {
                if (found) break;
                foreach (float s in new[] { .22f, -.22f, .3f, -.3f, .14f, -.14f, .38f, -.38f, 0f })
                {
                    if (found) break;
                    foreach (float f in new[] { 0f, -.08f, .08f, -.16f, .16f })
                    {
                        Vector3 p = front + side * s + ahead * f;
                        var surface = SurfaceAt(p);
                        if (surface == null || (surface.IsFloor && !floorToo) || !surface.Contains(new Vector2(p.x, p.z), radius + .01f)) continue;
                        if (desk && surface != WorkSurface) continue;
                        p.y = surface.Point(new Vector2(p.x, p.z)).y;
                        if (Head != null && p.y > Head.position.y - .25f) continue;
                        if (busy.Any(b => Mathf.Abs(b.at.y - p.y) < .2f && Vector3.ProjectOnPlane(b.at - p, Vector3.up).magnitude < b.radius + radius + .02f)) continue;
                        if (!FrameSpotClear(p, radius)) continue; // not over a monitor, box or lamp standing on the desk
                        best = p;
                        found = true;
                        break;
                    }
                }
            }
            // Nothing free right in front: the nearest free spot of the working desk, in widening rings: first
            // clear of everything; then (F51, a full desk) clear of what cannot step aside, and the crew and the
            // mic make room; last only clear of the room's own things.
            string how = found ? "in front" : "";
            foreach (int pass in new[] { 0, 1, 2 })
                if (!found && desk)
                {
                    var keep = pass == 0 ? busy : pass == 1 ? stays : null;
                    for (float r = .1f; r <= .9f && !found; r += .06f)
                        for (int k = 0; k < 16 && !found; k++)
                        {
                            Vector3 q = front + Quaternion.Euler(0f, k * 22.5f, 0f) * side * r;
                            if (!WorkSurface.Contains(new Vector2(q.x, q.z), radius + .01f)) continue;
                            q.y = WorkSurface.Point(new Vector2(q.x, q.z)).y;
                            if (keep != null && keep.Any(b => Mathf.Abs(b.at.y - q.y) < .2f && Vector3.ProjectOnPlane(b.at - q, Vector3.up).magnitude < b.radius + radius + .02f)) continue;
                            if (!FrameSpotClear(q, radius)) continue;
                            best = q;
                            found = true;
                            how = pass == 0 ? "free desk spot" : pass == 1 ? "desk spot, the crew and the mic make room" : "desk spot clear of room things";
                        }
                }
            if (!found) { best = WorkPoint(FrontPoint(.4f, -.35f, out _) + side * .25f, radius + .02f); how = "fallback"; }
            // The mic in it (even one the person put there) moves out: the frame is where a real thing is shown.
            if (arrival != null && !arrival.MicHeld && Mathf.Abs(arrival.MicRest.y - WindupsArrival.MicBase - best.y) < .2f &&
                Vector3.ProjectOnPlane(arrival.MicRest - best, Vector3.up).magnitude < radius + .05f) arrival.MicMakeRoom();
            Debug.Log("[Maker] show-me frame at " + best.ToString("F2") + " (" + how + ", desk " + desk + ")");
            Vector3 away = Head != null ? Vector3.ProjectOnPlane(best - Head.position, Vector3.up) : Vector3.forward;
            away = away.sqrMagnitude > 1e-5f ? away.normalized : Vector3.forward;
            Pose rest = new Pose(best + Vector3.up * .003f, Quaternion.LookRotation(Vector3.down, away));
            ShowDeskY = best.y;
            FrameLying = true;
            EndFrameDrag(false);
            handInFrame = handWasInFrame = false;
            frameHintSaid = false;
            StartCoroutine(FlyFrame(rest, true));
        }

        // Spark hands it over: out of Spark onto its place (or back into Spark, and gone).
        private IEnumerator FlyFrame(Pose rest, bool arriving)
        {
            int flight = ++frameFlight;
            frameFlying = true;
            frameArriving = arriving;
            frameRestAt = rest.position;
            Vector3 spark = cast.TryGetValue(Crew.Spark, out var s) && s != null ? s.transform.position + Vector3.up * .09f : rest.position + Vector3.up * .1f;
            Vector3 from = arriving ? spark : showFrame.position, to = arriving ? rest.position : spark;
            Quaternion fromRot = arriving ? SpeechBubbleDock.FacingAt(spark) : showFrame.rotation, toRot = arriving ? rest.rotation : SpeechBubbleDock.FacingAt(spark);
            float fromScale = arriving ? .12f : 1f, toScale = arriving ? 1f : .12f;
            showFrame.SetPositionAndRotation(from, fromRot);
            showFrame.localScale = Vector3.one * fromScale;
            showFrame.gameObject.SetActive(true);
            if (arriving) { Body(Crew.Spark)?.Play("Decision_Present"); ToySounds.Play(ToySounds.Cue.Whoosh, from, .3f, 1.3f); }
            for (float t = 0f; t < 1f && showFrame != null; t += Time.deltaTime / .5f)
            {
                if (flight != frameFlight) yield break; // put out again, or taken back, meanwhile
                float e = Mathf.SmoothStep(0f, 1f, t);
                showFrame.position = Vector3.Lerp(from, to, e) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .08f;
                showFrame.rotation = Quaternion.Slerp(fromRot, toRot, e);
                showFrame.localScale = Vector3.one * Mathf.Lerp(fromScale, toScale, e);
                yield return null;
            }
            if (flight != frameFlight) yield break;
            frameFlying = false;
            frameArriving = false;
            if (showFrame == null) yield break;
            if (!arriving) { showFrame.gameObject.SetActive(false); showFrame.localScale = Vector3.one; yield break; }
            showFrame.SetPositionAndRotation(rest.position, rest.rotation);
            showFrame.localScale = Vector3.one;
            ToySounds.Play(ToySounds.Cue.Pop, rest.position, .3f, 1.3f);
        }

        // `now`: gone at once (starting over: the session's coroutines were stopped, nothing would fly it back).
        private void HideShowFrame(bool now = false)
        {
            if (!FrameShown || (!now && frameFlying && !frameArriving)) return; // (not there, or already on its way back)
            EndFrameDrag(false);
            frameScan.gameObject.SetActive(false);
            shotPreview.gameObject.SetActive(false);
            if (now || frameFlying || !isActiveAndEnabled) { showFrame.gameObject.SetActive(false); showFrame.localScale = Vector3.one; frameFlying = frameArriving = false; frameFlight++; return; }
            StartCoroutine(FlyFrame(default, false));
        }

        // Nothing of the room stands where the frame will lie: rays down over it meet only the desk itself (a thing
        // already on the desk would look like something shown), and the eyes see it.
        private bool FrameSpotClear(Vector3 centre, float radius)
        {
            var room = arrival != null ? arrival.handoff.meeting.environment : null;
            if (room == null) return true;
            int mask = room.GeometryMask;
            if (Physics.CheckBox(centre + Vector3.up * .21f, new Vector3(radius + .01f, .2f, radius + .01f), Quaternion.identity, mask, QueryTriggerInteraction.Ignore)) return false;
            for (int i = 0; i < 13; i++)
            {
                Vector3 o = i == 0 ? Vector3.zero : Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward * (i % 2 == 0 ? radius + .015f : radius * .55f);
                Vector3 top = centre + o + Vector3.up * .6f;
                if (Physics.Raycast(top, Vector3.down, out var hit, .7f, mask, QueryTriggerInteraction.Ignore) && hit.point.y > centre.y + .008f) return false;
            }
            if (Head == null) return true;
            for (int i = 0; i < 5; i++)
            {
                Vector3 target = centre + (i == 0 ? Vector3.zero : Quaternion.Euler(0f, i * 90f, 0f) * Vector3.forward * radius * .8f) + Vector3.up * .004f;
                Vector3 d = target - Head.position;
                if (Physics.Raycast(Head.position, d.normalized, out var hit, d.magnitude - .01f, mask, QueryTriggerInteraction.Ignore)) return false;
            }
            return true;
        }

        private void PulseFrame() => framePulseAt = Time.time;
        private void PulseFrameButton()
        {
            buttonPulseAt = Time.time;
            FrameButtonPulses++;
            if (frameButton != null) ToySounds.Play(ToySounds.Cue.Pop, frameButton.position, .2f, 1.9f);
        }

        // ------------------------------------------------------------ where a pinch or a ray meets it

        private Vector3 FrameLocal(Vector3 world) => showFrame.InverseTransformPoint(world);

        // How far a point of the frame's plane is from the opening's outline.
        private float RimDistance(Vector3 local)
        {
            float dx = Mathf.Abs(local.x) - FrameSize.x * .5f, dy = Mathf.Abs(local.y) - FrameSize.y * .5f;
            return dx < 0f && dy < 0f ? Mathf.Min(-dx, -dy) : new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude;
        }

        // F60b: the band that takes a pinch: up to FrameGripOut outside the outline, FrameGripIn inside it.
        private bool OnRimBand(Vector3 local, float slack = 0f)
        {
            bool inside = Mathf.Abs(local.x) < FrameSize.x * .5f && Mathf.Abs(local.y) < FrameSize.y * .5f;
            return RimDistance(local) < (inside ? FrameGripIn : FrameGripOut) + slack;
        }

        // F68 (Quest 2026-11-14: "I can't lift the capture frame, it's too hard"): lying on a desk its rim is a thin
        // strip against the desk, so a pinch there pressed the fingers into the desk. While it lies, a pinch that
        // STARTS anywhere over it up to FrameGripDepth above takes it (over the opening: from OpeningLiftAbove up).
        // Something carried into it in a pinch is not a new pinch, and letting it go there starts none.
        // Over the opening the pinch must start OpeningLiftAbove over the desk (F56: a hand down at the desk in the
        // opening is putting something there, not taking the frame).
        public const float OpeningLiftAbove = .025f;
        private bool OverLyingFrame(Vector3 local)
        {
            if (!FrameLying || Mathf.Abs(local.z) >= FrameGripDepth) return false;
            bool inOpening = Mathf.Abs(local.x) < FrameSize.x * .5f && Mathf.Abs(local.y) < FrameSize.y * .5f;
            if (inOpening) return Mathf.Abs(local.z) > OpeningLiftAbove;
            return Mathf.Abs(local.x) < FrameSize.x * .5f + FrameGripOut && Mathf.Abs(local.y) < FrameSize.y * .5f + FrameGripOut;
        }

        // F73: standing (in the air or held up), its opening takes a pinch too, up to FrameOpeningDepth off its plane.
        private bool InOpeningStanding(Vector3 local) =>
            !FrameLying && Mathf.Abs(local.x) < FrameSize.x * .5f && Mathf.Abs(local.y) < FrameSize.y * .5f && Mathf.Abs(local.z) < FrameOpeningDepth;

        private Vector3 HandleLocal() => new Vector3(FrameSize.x * .5f + .015f, -FrameSize.y * .5f - .015f, -.002f);
        public Vector3 FrameHandlePoint => showFrame.TransformPoint(HandleLocal());

        private bool FrameRayPoint(Ray ray, out Vector3 world, out float distance)
        {
            world = Vector3.zero;
            distance = 0f;
            if (!new Plane(showFrame.forward, showFrame.position).Raycast(ray, out distance) || distance <= 0f || distance > 4f) return false;
            world = ray.GetPoint(distance);
            return true;
        }

        private bool FrameButtonHit(Vector3 point, Ray? ray)
        {
            if (!float.IsInfinity(point.x) && Vector3.Distance(point, frameButton.position) < .028f) return true;
            if (ray.HasValue && FrameRayPoint(ray.Value, out var at, out _) && Vector3.Distance(at, frameButton.position) < .02f) return true;
            return false;
        }

        private bool FrameHandleHit(Vector3 point, Ray? ray, out float far)
        {
            far = -1f;
            if (!float.IsInfinity(point.x) && Vector3.Distance(point, FrameHandlePoint) < FrameHandleReach) return true;
            if (ray.HasValue && FrameRayPoint(ray.Value, out var at, out float d) && Vector3.Distance(at, FrameHandlePoint) < .02f + d * .02f) { far = d; return true; }
            return false;
        }

        private bool FrameRimHit(Vector3 point, Ray? ray, out float far)
        {
            far = -1f;
            if (!float.IsInfinity(point.x))
            {
                Vector3 l = FrameLocal(point);
                if ((Mathf.Abs(l.z) < FrameGripDepth && OnRimBand(l)) || OverLyingFrame(l) || InOpeningStanding(l)) return true;
            }
            if (ray.HasValue && FrameRayPoint(ray.Value, out var at, out float d) && RimDistance(FrameLocal(at)) < .02f + d * .02f) { far = d; return true; }
            return false;
        }

        // RemoteGrab and the Editor leave rays on the frame to the session.
        private bool FrameOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            if (!FrameShown || frameFlying) return false;
            if (!FrameRayPoint(ray, out var at, out distance)) return false;
            return Vector3.Distance(at, frameButton.position) < .02f || Vector3.Distance(at, FrameHandlePoint) < .02f + distance * .02f ||
                   RimDistance(FrameLocal(at)) < .02f + distance * .02f;
        }

        // A new pinch: its button, the handle (the size) or its rim, corners included (carrying it). Near pinches come
        // first in the session's routing; the hand's ray only when nothing nearer took the pinch (`ray` is null in the
        // near pass).
        private bool ShowFramePinch(Hand hand, Vector3 point, Ray? ray)
        {
            if (!FrameShown || frameFlying) return false;
            if (frameMode == FrameMode.Carry) return FrameSecondHand(hand, point);
            if (frameMode != FrameMode.None) return false;
            if (FrameButtonHit(point, ray)) { PressFrameButton(); return true; }
            if (capturingShow) return false;
            if (FrameHandleHit(point, ray, out float handleFar)) { BeginFrameResize(hand, 1, handleFar, point, ray); return true; }
            if (!FrameRimHit(point, ray, out float far)) return false;
            BeginFrameCarry(hand, point, far, ray);
            return true;
        }

        // The other hand pinches the rim while one carries the frame: both stretch it.
        private bool FrameSecondHand(Hand hand, Vector3 point)
        {
            if (frameMode != FrameMode.Carry || hand == null || frameHand == null || hand == frameHand || frameFar > 0f || float.IsInfinity(point.x)) return false;
            Vector3 l = FrameLocal(point);
            if (Mathf.Abs(l.z) > FrameSecondReach + .02f || Mathf.Abs(l.x) > FrameSize.x * .5f + FrameSecondReach || Mathf.Abs(l.y) > FrameSize.y * .5f + FrameSecondReach) return false;
            frameMode = FrameMode.Stretch;
            frameHand2 = hand;
            FrameStretches++;
            stretchA0 = PinchPoint(frameHand);
            stretchB0 = point;
            stretchMid0 = (stretchA0 + stretchB0) * .5f;
            stretchCentre0 = showFrame.position;
            stretchSize0 = FrameSize;
            ToySounds.Play(ToySounds.Cue.Press, point, .3f, 1.8f);
            WriteInputLog("frame stretch with both hands", true);
            Heard();
            return true;
        }

        // ------------------------------------------------------------ carrying it, changing its size

        private void BeginFrameCarry(Hand hand, Vector3 point, float far, Ray? ray)
        {
            frameMode = FrameMode.Carry;
            frameHand = hand;
            frameHand2 = null;
            frameMouse = hand == null && !frameSim;
            frameFar = far;
            FrameGrabs++;
            frameSettleRun++; // (a settle still running from the last let-go stops)
            frameHeldSince = Time.time;
            frameOpenSince = -1f;
            frameFlyStart = -1f;
            if (hand != null && !frameSim)
            {
                // F73: held rigidly by the hand: the taken point and the turn stay with the fingers and the wrist.
                Vector3 taken = far > 0f && ray.HasValue ? ray.Value.GetPoint(far) : point;
                frameHoldHand = HandTurn(hand);
                frameHoldOffset = Quaternion.Inverse(frameHoldHand) * (showFrame.position - taken);
                frameHoldTurn = Quaternion.Inverse(frameHoldHand) * showFrame.rotation;
                if (far > 0f)
                {
                    // From afar it flies into the hand, then it is carried like one taken there.
                    frameFlyFrom = new Pose(showFrame.position, showFrame.rotation);
                    frameFlyStart = Time.time;
                    frameFar = -1f;
                }
                WriteInputLog("frame taken " + (far > 0f ? "from afar (flies to the hand)" : "in the hand") + " at " + (showFrame.position).ToString("F2") + (FrameLying ? ", lying" : ", standing"), true);
            }
            else if (ray.HasValue)
            {
                Vector3 at = ray.Value.GetPoint(Mathf.Max(far, .05f));
                frameGrabLocal = showFrame.position - at;
                frameMousePlane = new Plane(-ray.Value.direction, at);
            }
            FrameLying = false;
            ToySounds.Play(ToySounds.Cue.Press, showFrame.position, .3f, 1.5f);
            Heard();
        }

        private void BeginFrameResize(Hand hand, int corner, float far, Vector3 point = default, Ray? ray = null)
        {
            frameMode = FrameMode.Resize;
            frameHand = hand;
            frameMouse = hand == null && !frameSim;
            frameFar = far;
            frameCorner = corner;
            frameAnchor = FrameCorner((corner + 2) % 4);
            // F60b: where it was taken stays with the hand (the corner used to jump to the pinch).
            Vector3 taken = point;
            if (far > 0f && ray.HasValue) taken = ray.Value.GetPoint(far);
            frameHandleOffset = !frameSim && !float.IsInfinity(taken.x) && taken != default ? FrameCorner(corner) - taken : Vector3.zero;
            if (ray.HasValue && hand == null) frameMousePlane = new Plane(showFrame.forward, showFrame.position);
            FrameResizes++;
            ToySounds.Play(ToySounds.Cue.Press, FrameCorner(corner), .3f, 1.8f);
            Heard();
        }

        // The pulled corner goes to `world` (as far as the size limits let it); the opposite corner stays.
        private void ResizeFrameTo(Vector3 world)
        {
            Vector3 local = Quaternion.Inverse(showFrame.rotation) * (world - frameAnchor);
            float w = Mathf.Clamp(CornerX[frameCorner] * local.x, FrameMinSide, FrameMaxSide), h = Mathf.Clamp(CornerY[frameCorner] * local.y, FrameMinSide, FrameMaxSide);
            FrameSize = new Vector2(w, h);
            showFrame.position = frameAnchor + showFrame.rotation * new Vector3(CornerX[frameCorner] * w * .5f, CornerY[frameCorner] * h * .5f, 0f);
            LayoutFrame();
        }

        // Where a carried frame goes and how it turns: over a surface (within FrameLieAbove) it lies flat on it,
        // elsewhere it stands facing the person.
        private Pose CarryPose(Vector3 to)
        {
            Vector3 away = Head != null ? to - Head.position : Vector3.forward;
            Vector3 flat = Vector3.ProjectOnPlane(away, Vector3.up);
            if (flat.sqrMagnitude < 1e-5f) flat = Vector3.forward;
            var surface = SurfaceAt(to + Vector3.up * .03f);
            if (surface != null)
            {
                float y = surface.Point(new Vector2(to.x, to.z)).y;
                if (to.y - y < FrameLieAbove && to.y - y > -.04f)
                    return new Pose(new Vector3(to.x, y + .006f, to.z), Quaternion.LookRotation(Vector3.down, flat.normalized));
            }
            return new Pose(to, Quaternion.LookRotation(away.sqrMagnitude > 1e-5f ? away.normalized : Vector3.forward, Vector3.up));
        }

        private void FrameDragFrame()
        {
            bool live;
            Vector3 point = Vector3.positiveInfinity;
            Ray? ray = null;
            if (frameSim) live = true;
            else if (frameHand != null)
            {
                live = frameHand.isActiveAndEnabled && HandIO.Connected(frameHand) && HandIO.Pinching(frameHand);
                if (live)
                {
                    point = PinchPoint(frameHand);
                    if (frameFar > 0f && HandIO.Pointer(frameHand, out Pose pointer)) ray = new Ray(pointer.position, pointer.forward);
                }
                // F73: carried by a hand it is held rigidly while the hand pinches; fingers open for less than
                // FrameDropGrace (tracking blinking) keep it, standing still meanwhile.
                if (frameMode == FrameMode.Carry)
                {
                    if (live) frameOpenSince = -1f;
                    else if (frameOpenSince < 0f) frameOpenSince = Time.time;
                    if (!live && Time.time - frameOpenSince >= FrameDropGrace) { EndFrameDrag(true); return; }
                    if (live) HoldInHand(point);
                    LandingFrame();
                    return;
                }
            }
            else
            {
                live = false;
#if UNITY_EDITOR
                var mouse = UnityEngine.InputSystem.Mouse.current;
                var camera = Camera.main;
                live = frameMouse && mouse != null && camera != null && mouse.leftButton.isPressed;
                if (live) ray = camera.ScreenPointToRay(mouse.position.ReadValue());
#endif
            }
            if (frameMode == FrameMode.Stretch) { StretchFrame(live, point); return; }
            if (!live) { EndFrameDrag(true); return; }
            float blend = 1f - Mathf.Exp(-Time.deltaTime * 24f);
            if (frameMode == FrameMode.Carry)
            {
                if (frameSim) { showFrame.SetPositionAndRotation(frameSimPose.position, frameSimPose.rotation); return; }
                Vector3 to = showFrame.position;
                if (frameHand != null && frameFar <= 0f) to = point + frameGrabLocal;
                else if (ray.HasValue && frameHand != null) to = ray.Value.GetPoint(frameFar) + frameGrabLocal;
                else if (ray.HasValue && frameMousePlane.Raycast(ray.Value, out float d)) to = ray.Value.GetPoint(d) + frameGrabLocal;
                var pose = CarryPose(to);
                showFrame.SetPositionAndRotation(Vector3.Lerp(showFrame.position, pose.position, blend), Quaternion.Slerp(showFrame.rotation, pose.rotation, 1f - Mathf.Exp(-Time.deltaTime * 12f)));
                return;
            }
            Vector3 pull;
            if (frameSim) pull = frameSimPoint;
            else if (frameHand != null && frameFar <= 0f) pull = point + frameHandleOffset;
            else if (ray.HasValue && FrameRayPoint(ray.Value, out var at, out _)) pull = at + frameHandleOffset;
            else return;
            ResizeFrameTo(pull);
        }

        // Two hands on the rim: their spread across the frame sets its width, up and down its height; their middle
        // moves it. One hand lets go: the other carries it on; both: it is put down.
        private void StretchFrame(bool firstLive, Vector3 a)
        {
            bool secondLive = frameHand2 != null && frameHand2.isActiveAndEnabled && HandIO.Connected(frameHand2) && HandIO.Pinching(frameHand2);
            if (!firstLive || !secondLive)
            {
                var keep = firstLive ? frameHand : secondLive ? frameHand2 : null;
                WriteInputLog("frame size " + (FrameSize.x * 100f).ToString("0") + " x " + (FrameSize.y * 100f).ToString("0") + " cm (both hands)", true);
                if (keep == null) { EndFrameDrag(true); return; }
                frameMode = FrameMode.Carry;
                frameHand = keep;
                frameHand2 = null;
                frameGrabLocal = showFrame.position - PinchPoint(keep);
                // F73: the hand that stays holds it rigidly from here (as if it had just taken it there).
                frameHoldHand = HandTurn(keep);
                frameHoldOffset = Quaternion.Inverse(frameHoldHand) * (showFrame.position - PinchPoint(keep));
                frameHoldTurn = Quaternion.Inverse(frameHoldHand) * showFrame.rotation;
                frameOpenSince = frameFlyStart = -1f;
                frameHeldSince = Time.time;
                FrameLying = false;
                return;
            }
            Vector3 b = PinchPoint(frameHand2);
            Vector3 right = showFrame.right, up = showFrame.up;
            Vector3 sep = b - a, sep0 = stretchB0 - stretchA0;
            float w = Mathf.Clamp(stretchSize0.x + Mathf.Abs(Vector3.Dot(sep, right)) - Mathf.Abs(Vector3.Dot(sep0, right)), FrameMinSide, FrameMaxSide);
            float h = Mathf.Clamp(stretchSize0.y + Mathf.Abs(Vector3.Dot(sep, up)) - Mathf.Abs(Vector3.Dot(sep0, up)), FrameMinSide, FrameMaxSide);
            FrameSize = new Vector2(w, h);
            Vector3 centre = stretchCentre0 + ((a + b) * .5f - stretchMid0);
            if (FrameLying) centre.y = stretchCentre0.y; // lying on the desk it stays on the desk
            showFrame.position = centre;
            LayoutFrame();
        }

        private void EndFrameDrag(bool settle)
        {
            var mode = frameMode;
            bool byHand = frameHand != null && !frameSim;
            float held = Time.time - frameHeldSince;
            frameMode = FrameMode.None;
            frameHand = frameHand2 = null;
            frameMouse = frameSim = false;
            frameFar = -1f;
            frameCorner = -1;
            frameOpenSince = frameFlyStart = -1f;
            HideLanding();
            if (!settle || showFrame == null || mode == FrameMode.None) return;
            if (mode == FrameMode.Carry || (mode == FrameMode.Stretch && !FrameLying)) LayFrameDown(byHand && mode == FrameMode.Carry);
            else if (FrameLying) ShowDeskY = showFrame.position.y - .003f;
            if (mode == FrameMode.Carry) { FrameDrops++; LastFrameRelease = FrameLying ? "on a surface" : "in the air"; }
            ToySounds.Play(ToySounds.Cue.Pop, showFrame.position, .25f, mode == FrameMode.Carry ? 1.1f : 1.6f);
            WriteInputLog("frame " + (mode == FrameMode.Carry ? "put " + (FrameLying ? "on a surface" : "in the air") + (byHand ? " after " + held.ToString("0.0") + " s in the hand, tilt " + Vector3.Angle(FrameFront, Vector3.up).ToString("0") + " deg" : "")
                : "size " + (FrameSize.x * 100f).ToString("0") + " x " + (FrameSize.y * 100f).ToString("0") + " cm") + " at " + showFrame.position.ToString("F2"), true);
            if (FrameCrowdsMic()) { arrival.MicMakeRoom(); FrameMovedMic++; WriteInputLog("mic makes room for the frame", true); }
        }

        // F79: a frame put down or grown on the desk over the mic (its body or within reach of the size handle) makes
        // the mic step aside at once: a pinch at the handle went to the mic standing right by it.
        public const float FrameCornerFoot = .02f, FrameHandleFoot = .03f, FrameMicMargin = .045f;
        public int FrameMovedMic { get; private set; } // (checks)
        private bool FrameCrowdsMic()
        {
            if (arrival == null || !arrival.MicShown || arrival.MicHeld || showFrame == null || !FrameLying) return false;
            Vector3 mic = arrival.MicRest - Vector3.up * WindupsArrival.MicBase;
            if (Mathf.Abs(mic.y - ShowDeskY) > .1f) return false;
            Vector3 d = Vector3.ProjectOnPlane(mic - ShowCircleCentre, Vector3.up);
            Vector3 across = Vector3.ProjectOnPlane(showFrame.right, Vector3.up), along = Vector3.ProjectOnPlane(showFrame.up, Vector3.up);
            if (across.sqrMagnitude > 1e-6f && along.sqrMagnitude > 1e-6f &&
                Mathf.Abs(Vector3.Dot(d, across.normalized)) < FrameSize.x * .5f + FrameMicMargin &&
                Mathf.Abs(Vector3.Dot(d, along.normalized)) < FrameSize.y * .5f + FrameMicMargin) return true;
            return Vector3.ProjectOnPlane(mic - FrameHandlePoint, Vector3.up).magnitude < FrameMicMargin + FrameHandleFoot;
        }

        // Let go: close to a surface and roughly flat, it lies down on it; else it stays as it is, in the air.
        // F73: let go by a hand it settles there in FrameSettleTime (the hand left it a little tilted and above).
        private void LayFrameDown(bool glide = false)
        {
            FrameLying = false;
            ShowDeskY = showFrame.position.y;
            if (!LandingSpot(out Pose pose, out float deskY)) return;
            ShowDeskY = deskY;
            FrameLying = true;
            int run = ++frameSettleRun;
            if (glide && FrameSettleTime > 0f && isActiveAndEnabled) StartCoroutine(SettleFrame(pose, run));
            else showFrame.SetPositionAndRotation(pose.position, pose.rotation);
        }

        private IEnumerator SettleFrame(Pose to, int run)
        {
            Vector3 p0 = showFrame.position;
            Quaternion r0 = showFrame.rotation;
            for (float t = 0f; t < 1f; t += Time.deltaTime / FrameSettleTime)
            {
                if (run != frameSettleRun || showFrame == null || frameMode != FrameMode.None || frameFlying) yield break;
                float s = Mathf.SmoothStep(0f, 1f, t);
                showFrame.SetPositionAndRotation(Vector3.Lerp(p0, to.position, s), Quaternion.Slerp(r0, to.rotation, s));
                yield return null;
            }
            if (run == frameSettleRun && showFrame != null && frameMode == FrameMode.None && !frameFlying) showFrame.SetPositionAndRotation(to.position, to.rotation);
        }

        // F73: where it would lie if let go now: its middle within FrameLieAbove over a surface and its face within
        // FrameLieTilt of flat (either side up). Lying, the button edge keeps pointing the way it points now.
        private bool LandingSpot(out Pose pose, out float deskY)
        {
            pose = default;
            deskY = 0f;
            Vector3 at = showFrame.position;
            var surface = SurfaceAt(at + Vector3.up * .03f);
            if (surface == null) return false;
            deskY = surface.Point(new Vector2(at.x, at.z)).y;
            float tilt = Vector3.Angle(FrameFront, Vector3.up);
            if (at.y - deskY > FrameLieAbove || at.y - deskY < -.04f || (tilt > FrameLieTilt && tilt < 180f - FrameLieTilt)) return false;
            Vector3 up = Vector3.ProjectOnPlane(showFrame.up, Vector3.up);
            if (up.sqrMagnitude < 1e-4f) up = Head != null ? Vector3.ProjectOnPlane(at - Head.position, Vector3.up) : Vector3.forward;
            if (up.sqrMagnitude < 1e-6f) up = Vector3.forward;
            pose = new Pose(new Vector3(at.x, deskY + .003f, at.z), Quaternion.LookRotation(Vector3.down, up.normalized));
            return true;
        }

        // F73: held in the hand: the taken point stays at the fingers and the frame keeps its turn to the wrist (the
        // wrist's turn is smoothed a little against tracking jitter; the position is not).
        private void HoldInHand(Vector3 pinch)
        {
            frameHoldHand = Quaternion.Slerp(frameHoldHand, HandTurn(frameHand), 1f - Mathf.Exp(-Time.deltaTime * 30f));
            Vector3 position = pinch + frameHoldHand * frameHoldOffset;
            Quaternion rotation = frameHoldHand * frameHoldTurn;
            if (frameFlyStart >= 0f)
            {
                float t = (Time.time - frameFlyStart) / FrameFlyIn;
                if (t < 1f)
                {
                    float s = Mathf.SmoothStep(0f, 1f, t);
                    position = Vector3.Lerp(frameFlyFrom.position, position, s);
                    rotation = Quaternion.Slerp(frameFlyFrom.rotation, rotation, s);
                }
                else frameFlyStart = -1f;
            }
            showFrame.SetPositionAndRotation(position, rotation);
        }

        // F73: while carried, a mint outline on the surface shows where it will lie if let go now.
        private void LandingFrame()
        {
            bool show = LandingSpot(out Pose pose, out _);
            if (show && frameLanding == null) BuildLanding();
            if (frameLanding == null) return;
            if (frameLanding.gameObject.activeSelf != show) frameLanding.gameObject.SetActive(show);
            if (!show) return;
            frameLanding.SetPositionAndRotation(pose.position - Vector3.up * .001f, pose.rotation);
            float w = FrameSize.x, h = FrameSize.y, bar = .005f;
            landingBars[0].localPosition = new Vector3(0f, -h * .5f, 0f);
            landingBars[1].localPosition = new Vector3(0f, h * .5f, 0f);
            landingBars[2].localPosition = new Vector3(-w * .5f, 0f, 0f);
            landingBars[3].localPosition = new Vector3(w * .5f, 0f, 0f);
            landingBars[0].localScale = landingBars[1].localScale = new Vector3(w + bar, bar, 1f);
            landingBars[2].localScale = landingBars[3].localScale = new Vector3(h + bar, bar, 1f);
        }

        private void BuildLanding()
        {
            frameLanding = new GameObject("Frame Landing").transform;
            frameLanding.SetParent(transform, false);
            Color mint = new Color(.55f, 1f, .74f);
            var bar = StickerMesh("frameLandingBar", () => ToyMeshes.RoundedSlab(1f, 1f, .1f, .001f, mint, mint, mint, "FrameLandingBar"));
            for (int i = 0; i < 4; i++)
            {
                landingBars[i] = Part("Landing", bar);
                landingBars[i].SetParent(frameLanding, false);
                if (i >= 2) landingBars[i].localRotation = Quaternion.Euler(0f, 0f, 90f);
            }
            frameLanding.gameObject.SetActive(false);
        }

        private void HideLanding()
        {
            if (frameLanding != null && frameLanding.gameObject.activeSelf) frameLanding.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------ the size by voice

        // "Make the frame bigger", "smaller frame", "phone size", "laptop size", "paper size", "square frame".
        private bool FrameSizeVoice(string p)
        {
            if (!FrameShown || frameFlying || frameMode != FrameMode.None) return false;
            bool frame = p.Contains("frame") || p.Contains("틀");
            Vector2 size = FrameSize;
            string said;
            if (p.Contains("phone size") || (frame && (p.Contains("phone") || p.Contains("폰")))) { size = new Vector2(.09f, .17f); said = "phone"; }
            else if (p.Contains("laptop size") || (frame && (p.Contains("laptop") || p.Contains("노트북") || p.Contains("screen") || p.Contains("화면")))) { size = new Vector2(.34f, .23f); said = "laptop"; }
            else if (p.Contains("paper size") || (frame && (p.Contains("paper") || p.Contains("a4") || p.Contains("종이")))) { size = new Vector2(.3f, .22f); said = "paper"; }
            else if (frame && (p.Contains("square") || p.Contains("정사각"))) { float m = Mathf.Max(size.x, size.y); size = new Vector2(m, m); said = "square"; }
            else if (frame && (p.Contains("bigger") || p.Contains("larger") || p.Contains("크게") || p.Contains("키워"))) { size *= 1.3f; said = "bigger"; }
            else if (frame && (p.Contains("smaller") || p.Contains("작게") || p.Contains("줄여"))) { size *= .75f; said = "smaller"; }
            else return false;
            size = new Vector2(Mathf.Clamp(size.x, FrameMinSide, FrameMaxSide), Mathf.Clamp(size.y, FrameMinSide, FrameMaxSide));
            FrameSize = size;
            LayoutFrame();
            PulseFrame();
            ToySounds.Play(ToySounds.Cue.Pop, showFrame.position, .3f, 1.4f);
            WriteInputLog("frame size " + (size.x * 100f).ToString("0") + " x " + (size.y * 100f).ToString("0") + " cm (said " + said + ")", true);
            Ack(Crew.Spark, "There, " + (size.x * 100f).ToString("0") + " by " + (size.y * 100f).ToString("0") + " centimetres.");
            Heard();
            return true;
        }

        // ------------------------------------------------------------ every frame        // ------------------------------------------------------------ every frame

        private void ShowFrameFrame()
        {
            if (!FrameShown || frameFlying) return;
            if (frameMode != FrameMode.None) { frameHover = true; FrameDragFrame(); return; }
            frameHover = FrameHoverNow();
            if (capturingShow) { handInFrame = false; return; }
            FrameButtonTouch();
            FrameHandsHint();
        }

        // F60b: a free, open hand whose pinch point is on the rim band or by the handle: the rim swells.
        private bool FrameHoverNow()
        {
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || HandIO.Pinching(hand) || HeldBy(hand) != null) continue;
                Vector3 p = PinchPoint(hand);
                Vector3 l = FrameLocal(p);
                if ((Mathf.Abs(l.z) < FrameGripDepth + .01f && OnRimBand(l)) || OverLyingFrame(l) || InOpeningStanding(l) || Vector3.Distance(p, FrameHandlePoint) < FrameHandleReach) return true;
            }
            return false;
        }

        // A fingertip pushed onto the button (the hand not pinching: a pinch is a grab).
        private void FrameButtonTouch()
        {
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) { framePress.Forget(hand); continue; }
                bool on = HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip) && OnFrameButton(tip.position);
                if (framePress.Step(this, hand, on)) { PressLog("frame button", hand); PressFrameButton(); return; }
            }
        }

        private bool OnFrameButton(Vector3 world)
        {
            Vector3 l = frameButton.InverseTransformPoint(world);
            return Mathf.Abs(l.x) < .02f && Mathf.Abs(l.y) < .017f && l.z > -.03f && l.z < .012f;
        }

        // A hand went into the lying frame (to put something down) and came out: nothing is taken by itself
        // (that took blurred hands); the button bounces, and the first time Spark says to press it.
        public static float FrameHintView = 40f;
        public const string FrameHintLine = "Put something in my frame? Press the red button on the frame!";

        private void FrameHandsHint()
        {
            if (!FrameLying) { handInFrame = handWasInFrame = false; return; }
            bool inside = false;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandPalm, out Pose palm)) continue;
                // F86 (Quest: "nothing is over the green one's head, yet it says press my button"): a hand carrying
                // something (a drawing card to Maker, the sketchbook) only passes over the frame; it put nothing in.
                if (HandRules && HeldBy(hand) != null) continue;
                Vector3 l = FrameLocal(palm.position);
                if (Mathf.Abs(l.x) < FrameSize.x * .5f + .02f && Mathf.Abs(l.y) < FrameSize.y * .5f + .02f && l.z > -.10f && l.z < .03f) inside = true;
            }
            handInFrame = inside;
            if (inside)
            {
                if (handInSince < 0f) handInSince = Time.time;
                if (Time.time - handInSince > .3f) handWasInFrame = true;
                handLeftAt = -1f;
                return;
            }
            handInSince = -1f;
            if (!handWasInFrame) return;
            if (handLeftAt < 0f) handLeftAt = Time.time;
            if (Time.time - handLeftAt < .45f) return;
            handWasInFrame = false;
            handLeftAt = -1f;
            PulseFrameButton();
            if (frameHintSaid) return;
            // F86: say it only while the frame is in view, and name where the button is (it is the red shutter on the
            // frame, not a button over Spark's head).
            if (Head != null && Vector3.Angle(Head.forward, showFrame.position - Head.position) > FrameHintView) return;
            frameHintSaid = true;
            Ack(Crew.Spark, FrameHintLine);
        }

        // Some joint of a hand is over the lying frame (up to 35 cm above it) or on the line from the eyes to it.
        public bool HandsOverFrame()
        {
            if (Head == null || showFrame == null) return false;
            Vector3 eye = Head.position, centre = showFrame.position + FrameFront * .03f;
            float reach = Mathf.Min(FrameSize.x, FrameSize.y) * .45f;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) continue;
                foreach (var joint in CoverJoints)
                {
                    if (!HandIO.Joint(hand, joint, out Pose p)) continue;
                    Vector3 l = FrameLocal(p.position);
                    if (Mathf.Abs(l.x) < FrameSize.x * .5f + .03f && Mathf.Abs(l.y) < FrameSize.y * .5f + .03f && l.z < .02f && l.z > -.35f) return true;
                    Vector3 sight = centre - eye;
                    float t = Vector3.Dot(p.position - eye, sight) / sight.sqrMagnitude;
                    if (t > .15f && t < 1f && Vector3.Distance(eye + sight * t, p.position) < reach) return true;
                }
            }
            return false;
        }

        private void ShowFrameLate(float blend)
        {
            if (!FrameShown) return;
            // Breathes gently; pulses when someone shows it.
            float since = Time.time - framePulseAt;
            float pulse = frameFlying ? 1f : since < .9f ? 1f + .1f * Mathf.Sin(since / .9f * Mathf.PI * 2f) : 1f + .012f * Mathf.Sin(Time.time * 2f);
            frameBody.localScale = Vector3.Lerp(frameBody.localScale, Vector3.one * (frameMode == FrameMode.None ? pulse : 1f), blend);
            float swell = Mathf.MoveTowards(frameSwell, frameHover ? 1.5f : 1f, Time.deltaTime * 4f);
            if (!Mathf.Approximately(swell, frameSwell)) { frameSwell = swell; LayoutFrame(); }
            float b = Time.time - buttonPulseAt;
            float press = Time.time - framePressedAt < .25f ? .55f : 1f;
            frameDome.localScale = new Vector3(1f, 1f, press) * (b < .8f ? 1f + .35f * Mathf.Sin(b / .8f * Mathf.PI * 2f) * (1f - b / .8f) : 1f);
            if (showLabel != null) showLabel.rotation = SpeechBubbleDock.FacingAt(showLabel.position);
            string label = frameMode == FrameMode.Resize || frameMode == FrameMode.Stretch ? (FrameSize.x * 100f).ToString("0") + " x " + (FrameSize.y * 100f).ToString("0") + " cm"
                : capturingShow ? (ScanWaiting ? "Hands away!" : "Scanning...") : handInFrame ? "Put it here" : CircleIdleLabel; // F45: "Your mug here" first; F88: "Hands away!" before the scan
            if (showText != null && showText.text != label) showText.text = label;
            // F73: no label lying idle under it (the person said so of extra text panels): only the size while it changes
            // and "Scanning..." while the picture is taken. (Its words are kept for what reads them.)
            bool labelUp = frameMode == FrameMode.Resize || frameMode == FrameMode.Stretch || capturingShow;
            if (showLabel != null && showLabel.gameObject.activeSelf != labelUp) showLabel.gameObject.SetActive(labelUp);
            ScanLate();
            ScanHintLate(); // F88: "Touch to scan" over the red button until it is first pressed
            if (shotPreview.gameObject.activeSelf && Time.time > previewUntil) shotPreview.gameObject.SetActive(false);
            if (capturingShow && cast.TryGetValue(Crew.Spark, out var spark) && spark != null) spark.lookTarget = null;
        }

        // ------------------------------------------------------------ tests

        public string ShowLabelText => showText != null ? showText.text : "";
        public void SimulateHandInCircle(bool inside) { handInFrame = inside; }
        // Tests: carry the frame to a pose and let go there (the same settle as a real release).
        public void SimulateFrameCarry(Vector3 to, Quaternion rotation)
        {
            if (!FrameShown || frameFlying) return;
            frameSim = true;
            BeginFrameCarry(null, Vector3.positiveInfinity, -1f, null);
            frameSimPose = new Pose(to, rotation);
            FrameDragFrame();
            EndFrameDrag(true);
        }
        // Tests: pull corner `corner` to a point of the world and let go (the app's own handle is corner 1).
        public void SimulateFrameResize(int corner, Vector3 to)
        {
            if (!FrameShown || frameFlying) return;
            frameSim = true;
            BeginFrameResize(null, corner, -1f, Vector3.positiveInfinity);
            frameSimPoint = to;
            FrameDragFrame();
            EndFrameDrag(true);
        }
        public bool FrameFlying => frameFlying;
        public bool SimulateFrameSizeWords(string p) => FrameSizeVoice(p.ToLowerInvariant());
    }
}
