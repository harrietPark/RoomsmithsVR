using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // 2026-11-13 (the user: "a paint-tube button on the palette for ordinary colours, not only ones from the room - a colour
    // wheel?", then chose draft B, the smooth wheel, over a wheel of chips): a paint tube with a "+" sits on the palette's
    // rim. A fingertip press on it (MakerSession.HandGate FingerPress) opens a smooth colour wheel above the palette,
    // world-locked where it opened and facing the person, with a brightness bar beside it.
    //  - The right fingertip over the wheel shows the colour under it as a drop just above the fingertip (the finger
    //    never hides what it picks); the colour comes from the fingertip averaged over WheelSmoothing (hand jitter).
    //  - A pinch on the wheel picks: on release the colour from WheelReleaseLag before the release is taken (the
    //    fingers slide when a pinch opens) and flies into the palette as a new blob (AddSwatch, like a picked colour).
    //  - The fingertip on the bar sets the brightness; the wheel shows its colours at that brightness.
    //  - The tube again, the palm down for WheelPalmGrace or WheelIdle without use closes it.
    // Editor: a click on the tube opens it, a click on the wheel picks, on the bar sets the brightness.
    public sealed partial class MakerSession
    {
        public static float WheelRadius = .085f, WheelSmoothing = .15f, WheelReleaseLag = .1f, WheelIdle = 12f, WheelPalmGrace = 1.5f;
        // F95 (Quest 2026-11-15, the user: "one press should show the colour wheel; picking a paint hides it, or the
        // button again"): the log had the wheel open and close 0.1-0.8 s later five times in a row. The fingertip
        // rested at the edge of the button's 1.8 cm reach; each time it slipped out and back in was a second press.
        // Now it must go TubeLeave away before the button presses again, the wheel is not toggled again within
        // WheelToggleGap, and dipping a fingertip in a paint on the palette (LoadBrush) closes the wheel too.
        public static float TubeReach = .018f, TubeLeave = .04f, WheelToggleGap = .6f;
        private bool tubeFingerIn;
        private float wheelToggledAt = -10f;
        public int WheelToggles { get; private set; }
        public bool WheelOpen => wheelRoot != null && wheelRoot.gameObject.activeSelf;
        public int WheelPicks { get; private set; }
        public Color WheelColour { get; private set; } = Color.white;
        public float WheelValue => wheelValue;
        public Transform WheelTube => wheelTube;

        private Transform wheelTube, wheelRoot, wheelBar, wheelKnob, wheelDrop;
        private Mesh wheelMesh;
        private Material wheelLook, wheelDropLook;
        private float wheelValue = 1f, wheelShownValue = -1f, wheelUsedAt, wheelPalmGoneAt = -1f;
        private bool wheelHover, wheelPinching;
        private Hand wheelHand;
        private readonly FingerPress tubePress = new FingerPress();
        private readonly Queue<(float t, Vector2 xy)> wheelTrail = new Queue<(float, Vector2)>();
        private readonly Queue<(float t, Color c)> wheelPicked = new Queue<(float, Color)>();
        private const int WheelRings = 12, WheelSegments = 72;

        // ------------------------------------------------------------ every frame (SolidFrame)

        private void ColourWheelFrame()
        {
            if (!Begun || paletteBoard == null) return;
            EnsureWheelTube();
            var right = LiveHand(Handedness.Right);
            // The tube: a fingertip press toggles the wheel (only while the palette is up).
            // F95: the fingertip counts as on it from TubeReach and stays on until it is TubeLeave away (a lost
            // tracking frame changes nothing), so a finger resting at the button's edge is one press, not several.
            if (!paletteBoard.gameObject.activeInHierarchy) tubeFingerIn = false;
            else if (right != null && HandIO.Joint(right, HandJointId.HandIndexTip, out var tip))
                tubeFingerIn = Vector3.Distance(tip.position, wheelTube.position) < (tubeFingerIn ? TubeLeave : TubeReach);
            bool onTube = tubeFingerIn;
            if (tubePress.Step(this, right, onTube) && Time.time - wheelToggledAt >= WheelToggleGap)
            {
                ToySounds.Play(ToySounds.Cue.Press, wheelTube.position, .35f, 1.4f);
                WriteInputLog("press colour wheel button (" + (WheelOpen ? "close" : "open") + ")", true);
                ToggleWheel();
            }
            PickerButtonFrame(onTube); // 2026-11-15: the designed picker's button sinks and glows
#if UNITY_EDITOR
            EditorTubeClick();
#endif
            if (!WheelOpen) return;
            float now = Time.time;
            // closes: the palm down for a while, or unused
            if (!PaletteOnPalm) { if (wheelPalmGoneAt < 0f) wheelPalmGoneAt = now; else if (now - wheelPalmGoneAt > WheelPalmGrace) { CloseWheel(); return; } }
            else wheelPalmGoneAt = -1f;
            if (now - wheelUsedAt > WheelIdle) { CloseWheel(); return; }

            wheelHover = false;
            bool pinching = right != null && HandIO.Pinching(right);
            Vector3? point = null;
            if (right != null)
            {
                if (pinching) point = PinchPoint(right);
                else if (HandIO.Joint(right, HandJointId.HandIndexTip, out var finger)) point = finger.position;
            }
#if UNITY_EDITOR
            Vector3? mousePoint = EditorWheelPoint(out bool clicked);
            if (mousePoint.HasValue) { point = mousePoint; if (clicked) { WheelPointer(point.Value, now); CommitWheel(WheelColour); return; } }
#endif
            if (point.HasValue) WheelPointer(point.Value, now);
            if (WheelTouchFrame(right, pinching, now)) return;
            // pinch on the wheel: picks on release, with the colour from just before the fingers opened
            if (pinching && wheelHover && !wheelPinching) { wheelPinching = true; wheelHand = right; wheelTouching = false; }
            else if (wheelPinching && !pinching)
            {
                wheelPinching = false;
                Color c = WheelColour;
                foreach (var (t, col) in wheelPicked) if (t <= now - WheelReleaseLag) c = col;
                CommitWheel(c);
            }
            if (wheelDrop != null)
            {
                wheelDrop.gameObject.SetActive(wheelHover && point.HasValue);
                if (wheelHover && point.HasValue)
                {
                    wheelDrop.position = point.Value + wheelRoot.up * .024f - wheelRoot.forward * .006f;
                    wheelDropLook.SetColor("_BaseColor", WheelColour);
                }
            }
            if (Mathf.Abs(wheelShownValue - wheelValue) > .01f) PaintWheel();
        }

        // 2026-11-15 (Quest: "pinching works, but let me pick on the colour wheel by touching too"): a fingertip touch
        // picks as well. The free, pointing right fingertip on the wheel's face (from WheelTouchNear in front of it to
        // WheelTouchThrough behind it) touches; it may slide to the colour it wants (the drop above it shows it), and
        // lifting it off toward the person takes the colour from WheelReleaseLag before the lift, like a pinch let go.
        // Sliding off the wheel's edge, a pinch, or a touch shorter than WheelTouchDwell takes nothing.
        public static float WheelTouchNear = -.006f, WheelTouchLift = -.012f, WheelTouchThrough = .03f, WheelTouchDwell = .12f;
        public int WheelTouchPicks { get; private set; }
        private bool wheelTouching;
        private float wheelTouchAt, wheelTouchFrom;

        private bool WheelTouchFrame(Hand right, bool pinching, float now)
        {
            Vector3 l = Vector3.zero;
            Pose tip = default;
            bool tracked = right != null && HandIO.Joint(right, HandJointId.HandIndexTip, out tip);
            if (tracked) l = wheelRoot.InverseTransformPoint(tip.position);
            bool onDisc = tracked && new Vector2(l.x, l.y).magnitude <= WheelRadius * 1.02f;
            if (!wheelTouching)
            {
                // (not right after a pick: the finger of a pinch just let go is still on the wheel)
                if (tracked && onDisc && !pinching && !wheelPinching && now >= wheelTouchFrom && l.z >= WheelTouchNear && l.z <= WheelTouchThrough && Pointing(right) && HandFree(right))
                { wheelTouching = true; wheelTouchAt = now; wheelUsedAt = now; }
                return false;
            }
            if (pinching || !tracked) { wheelTouching = false; return false; } // (a pinch is the pinch pick)
            if (onDisc && l.z >= WheelTouchLift && l.z <= WheelTouchThrough + .01f) return false; // still touching
            wheelTouching = false;
            if (!onDisc || l.z > WheelTouchThrough || now - wheelTouchAt < WheelTouchDwell) return false; // slid off / through / a brush past
            Color c = WheelColour;
            foreach (var (t, col) in wheelPicked) if (t <= now - WheelReleaseLag) c = col;
            WheelTouchPicks++;
            WriteInputLog("colour wheel: touch pick", true);
            CommitWheel(c);
            return true;
        }

        // A fingertip (or pinch) over the wheel or the bar.
        private void WheelPointer(Vector3 world, float now)
        {
            Vector3 local = wheelRoot.InverseTransformPoint(world);
            if (Mathf.Abs(local.z) > .03f) return;
            Vector2 xy = new Vector2(local.x, local.y);
            // the brightness bar
            if (Mathf.Abs(local.x - (WheelRadius + .03f)) < .018f && Mathf.Abs(local.y) < WheelRadius + .01f)
            {
                float v = Mathf.Clamp01((local.y + WheelRadius) / (2f * WheelRadius));
                wheelValue = Mathf.Lerp(wheelValue, v, .35f);
                wheelKnob.localPosition = new Vector3(WheelRadius + .03f, Mathf.Lerp(-WheelRadius, WheelRadius, wheelValue), -.004f);
                wheelUsedAt = now;
                return;
            }
            if (xy.magnitude > WheelRadius * 1.04f) return;
            wheelHover = true;
            wheelUsedAt = now;
            wheelTrail.Enqueue((now, xy));
            while (wheelTrail.Count > 0 && wheelTrail.Peek().t < now - WheelSmoothing) wheelTrail.Dequeue();
            Vector2 sum = Vector2.zero;
            foreach (var (_, p) in wheelTrail) sum += p;
            Vector2 avg = sum / Mathf.Max(1, wheelTrail.Count);
            WheelColour = WheelAt(avg, wheelValue);
            wheelPicked.Enqueue((now, WheelColour));
            while (wheelPicked.Count > 0 && wheelPicked.Peek().t < now - .5f) wheelPicked.Dequeue();
        }

        // A pinch here belongs to the wheel (ToolsPinch gives it no room colour pick): on the wheel or its bar, or by the tube.
        private bool OnColourWheel(Vector3 world)
        {
            if (wheelTube != null && wheelTube.gameObject.activeInHierarchy && Vector3.Distance(world, wheelTube.position) < .025f) return true;
            if (!WheelOpen) return false;
            Vector3 local = wheelRoot.InverseTransformPoint(world);
            if (Mathf.Abs(local.z) > .04f) return false;
            if (new Vector2(local.x, local.y).magnitude < WheelRadius * 1.1f) return true;
            return Mathf.Abs(local.x - (WheelRadius + .03f)) < .022f && Mathf.Abs(local.y) < WheelRadius + .015f;
        }

        // The colour at a point of the wheel (its own space): hue by angle, saturation by distance from the middle.
        public static Color WheelAt(Vector2 xy, float value)
        {
            float hue = Mathf.Repeat(Mathf.Atan2(xy.y, xy.x) / (2f * Mathf.PI), 1f);
            float sat = Mathf.Clamp01(xy.magnitude / WheelRadius);
            return Color.HSVToRGB(hue, sat, value);
        }

        private void CommitWheel(Color c)
        {
            if (wheelRoot == null) return;
            WheelPicks++;
            wheelUsedAt = Time.time;
            wheelTouching = false;
            wheelTouchFrom = Time.time + .5f; // 2026-11-15: no touch pick from the same finger right after
            var swatch = AddSwatch("colour", c, ColourName(c));
            Talk.Add("Person took " + swatch.name + " from the colour wheel (" + swatch.hex + ")");
            WriteInputLog("colour wheel: " + swatch.hex + " " + swatch.name);
            Vector3 from = wheelDrop != null && wheelDrop.gameObject.activeSelf ? wheelDrop.position : wheelRoot.position;
            StartCoroutine(FlyDrop(from, c));
            // F81 (the user: "once I picked, the colour wheel should go away - toggle off"): one pick closes it.
            if (WheelClosesAfterPick) StartCoroutine(After(.2f, CloseWheel));
        }
        public static bool WheelClosesAfterPick = true;

        private IEnumerator FlyDrop(Vector3 from, Color c)
        {
            var drop = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(drop.GetComponent<Collider>());
            drop.name = "Wheel Drop";
            drop.transform.SetParent(transform, false);
            drop.transform.localScale = Vector3.one * .014f;
            var look = new Material(wheelDropLook) { name = "Wheel Drop" };
            look.SetColor("_BaseColor", c);
            drop.GetComponent<MeshRenderer>().sharedMaterial = look;
            ToySounds.Play(ToySounds.Cue.Drip, from, .4f, 1.2f);
            for (float t = 0f; t < 1f && paletteBoard != null; t += Time.deltaTime / .35f)
            {
                Vector3 to = paletteBoard.position;
                drop.transform.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t)) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .04f;
                yield return null;
            }
            if (paletteBoard != null) { ToyFx.Puff(paletteBoard.position, .006f, 3); ToySounds.Play(ToySounds.Cue.Splat, paletteBoard.position, .35f, 1.3f); }
            Destroy(look);
            Destroy(drop);
        }

        // ------------------------------------------------------------ open, close

        private void ToggleWheel()
        {
            wheelToggledAt = Time.time;
            WheelToggles++;
            if (WheelOpen) CloseWheel(); else OpenWheel();
        }

        public void OpenWheel()
        {
            if (paletteBoard == null) return;
            EnsureWheel();
            Vector3 eyes = Head != null ? Head.position : paletteBoard.position + Vector3.back * .4f;
            Vector3 toEyes = Vector3.ProjectOnPlane(eyes - paletteBoard.position, Vector3.up);
            toEyes = toEyes.sqrMagnitude > 1e-6f ? toEyes.normalized : Vector3.back;
            Vector3 viewRight = Vector3.Cross(Vector3.up, -toEyes);
            // above the palette and a little to the right, toward the free hand
            Vector3 at = paletteBoard.position + Vector3.up * .12f + toEyes * .02f + viewRight * .05f;
            wheelRoot.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at));
            wheelRoot.gameObject.SetActive(true);
            wheelRoot.localScale = Vector3.one * .05f;
            StartCoroutine(Grow(wheelRoot, 1f, .22f));
            wheelUsedAt = Time.time;
            wheelPalmGoneAt = -1f;
            wheelTrail.Clear();
            wheelPicked.Clear();
            wheelTouching = false;
            PaintWheel();
            ToySounds.Play(ToySounds.Cue.Pop, at, .45f, 1.3f);
            WriteInputLog("colour wheel opened");
        }

        public void CloseWheel()
        {
            if (!WheelOpen) return;
            ToyFx.Puff(wheelRoot.position, .01f, 3);
            wheelRoot.gameObject.SetActive(false);
            wheelPinching = false;
            wheelTouching = false;
            if (wheelDrop != null) wheelDrop.gameObject.SetActive(false);
            WriteInputLog("colour wheel closed");
        }

        // ------------------------------------------------------------ the pieces

        // A paint tube with a green "+" on the palette's rim (a child of the board, so it comes and goes with it).
        private void EnsureWheelTube()
        {
            if (wheelTube != null) return;
            wheelTube = new GameObject("Paint Tube +").transform;
            wheelTube.SetParent(paletteBoard, false);
            // 2026-11-15: on the designed palette the colour-picker clip's button (it wears the colour wheel) is the
            // press point; the tube is not drawn.
            if (pickerButton != null)
            {
                wheelTube.localPosition = PickerButtonLocal();
                wheelTube.localRotation = Quaternion.identity;
                return;
            }
            wheelTube.localPosition = new Vector3(.088f, .038f, -.006f);
            wheelTube.localRotation = Quaternion.Euler(0f, 0f, -55f);
            Transform Piece(string name, Mesh mesh, Vector3 local, Vector3 scale)
            {
                owned.Add(mesh);
                var t = Part(name, mesh);
                t.SetParent(wheelTube, false);
                t.localPosition = local;
                t.localScale = scale;
                return t;
            }
            Color white = new Color(.97f, .97f, .97f), grey = new Color(.62f, .62f, .62f), orange = new Color(1f, .54f, .24f), green = new Color(.18f, .56f, .36f);
            Piece("Body", ToyMeshes.RoundedSlab(.013f, .026f, .0055f, .009f, white, grey, white, "TubeBody"), Vector3.zero, Vector3.one);
            Piece("Band", ToyMeshes.RoundedSlab(.0132f, .006f, .001f, .0094f, orange, orange, orange, "TubeBand"), new Vector3(0f, .002f, 0f), Vector3.one);
            Piece("Cap", ToyMeshes.RoundedSlab(.007f, .006f, .0015f, .007f, new Color(.33f, .33f, .35f), new Color(.25f, .25f, .27f), new Color(.33f, .33f, .35f), "TubeCap"), new Vector3(0f, .016f, 0f), Vector3.one);
            Piece("Plus Dot", ToyMeshes.Disc(.0042f, .0016f, green, green, "TubePlusDot"), new Vector3(0f, -.007f, -.0055f), Vector3.one);
            Piece("Plus H", ToyMeshes.RoundedSlab(.0046f, .0011f, .0005f, .0006f, Color.white, Color.white, Color.white, "TubePlusH"), new Vector3(0f, -.007f, -.0065f), Vector3.one);
            Piece("Plus V", ToyMeshes.RoundedSlab(.0011f, .0046f, .0005f, .0006f, Color.white, Color.white, Color.white, "TubePlusV"), new Vector3(0f, -.007f, -.0065f), Vector3.one);
        }

        private void EnsureWheel()
        {
            if (wheelRoot != null) return;
            wheelRoot = new GameObject("Colour Wheel").transform;
            wheelRoot.SetParent(transform, false);
            // backing ring
            var back = ToyMeshes.Disc(WheelRadius + .008f, .004f, Color.white, new Color(.86f, .84f, .8f), "WheelBack");
            owned.Add(back);
            var b = Part("Back", back);
            b.SetParent(wheelRoot, false);
            b.localPosition = new Vector3(0f, 0f, .003f);
            // the wheel: vertex colours, shown as they are (True Colour, no shading)
            wheelMesh = new Mesh { name = "Colour Wheel" };
            owned.Add(wheelMesh);
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var t = new List<int>();
            v.Add(Vector3.zero); n.Add(Vector3.back);
            for (int r = 1; r <= WheelRings; r++)
                for (int s = 0; s < WheelSegments; s++)
                {
                    float a = s * Mathf.PI * 2f / WheelSegments, rad = WheelRadius * r / WheelRings;
                    v.Add(new Vector3(Mathf.Cos(a) * rad, Mathf.Sin(a) * rad, 0f));
                    n.Add(Vector3.back);
                }
            for (int s = 0; s < WheelSegments; s++) { t.Add(0); t.Add(1 + (s + 1) % WheelSegments); t.Add(1 + s); }
            for (int r = 1; r < WheelRings; r++)
                for (int s = 0; s < WheelSegments; s++)
                {
                    int a0 = 1 + (r - 1) * WheelSegments + s, a1 = 1 + (r - 1) * WheelSegments + (s + 1) % WheelSegments;
                    int b0 = a0 + WheelSegments, b1 = a1 + WheelSegments;
                    t.Add(a0); t.Add(a1); t.Add(b1);
                    t.Add(a0); t.Add(b1); t.Add(b0);
                }
            wheelMesh.SetVertices(v);
            wheelMesh.SetNormals(n);
            wheelMesh.SetTriangles(t, 0);
            wheelMesh.RecalculateBounds();
            var disc = new GameObject("Wheel", typeof(MeshFilter), typeof(MeshRenderer));
            disc.transform.SetParent(wheelRoot, false);
            disc.GetComponent<MeshFilter>().sharedMesh = wheelMesh;
            wheelLook = TrueTemplate != null ? new Material(TrueTemplate) : new Material(material);
            wheelLook.name = "Colour Wheel";
            if (wheelLook.HasProperty("_VertexColour")) wheelLook.SetFloat("_VertexColour", 1f);
            if (wheelLook.HasProperty("_Shade")) wheelLook.SetFloat("_Shade", 1f);
            if (wheelLook.HasProperty("_RimStrength")) wheelLook.SetFloat("_RimStrength", 0f);
            wheelLook.SetTexture("_BaseMap", Texture2D.whiteTexture);
            wheelLook.SetColor("_BaseColor", Color.white);
            owned.Add(wheelLook);
            var dr = disc.GetComponent<MeshRenderer>();
            dr.sharedMaterial = wheelLook;
            dr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // the brightness bar and its knob
            var bar = ToyMeshes.RoundedSlab(.02f, WheelRadius * 2f, .008f, .004f, new Color(.55f, .55f, .55f), new Color(.4f, .4f, .4f), new Color(.55f, .55f, .55f), "WheelBar");
            owned.Add(bar);
            wheelBar = Part("Brightness", bar);
            wheelBar.SetParent(wheelRoot, false);
            wheelBar.localPosition = new Vector3(WheelRadius + .03f, 0f, 0f);
            var shade = GradientBar();
            var sb = new GameObject("Brightness Shade", typeof(MeshFilter), typeof(MeshRenderer));
            sb.transform.SetParent(wheelRoot, false);
            sb.transform.localPosition = new Vector3(WheelRadius + .03f, 0f, -.0025f);
            sb.GetComponent<MeshFilter>().sharedMesh = shade;
            sb.GetComponent<MeshRenderer>().sharedMaterial = wheelLook;
            var knob = ToyMeshes.Disc(.011f, .006f, Color.white, new Color(.18f, .56f, .36f), "WheelKnob");
            owned.Add(knob);
            wheelKnob = Part("Knob", knob);
            wheelKnob.SetParent(wheelRoot, false);
            wheelKnob.localPosition = new Vector3(WheelRadius + .03f, WheelRadius, -.004f);
            // the drop that shows the colour under the fingertip
            wheelDropLook = TrueTemplate != null ? new Material(TrueTemplate) : new Material(material);
            wheelDropLook.name = "Wheel Drop";
            if (wheelDropLook.HasProperty("_Gloss")) wheelDropLook.SetFloat("_Gloss", .35f);
            owned.Add(wheelDropLook);
            var drop = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(drop.GetComponent<Collider>());
            drop.name = "Wheel Preview";
            drop.transform.SetParent(transform, false);
            drop.transform.localScale = Vector3.one * .018f;
            drop.GetComponent<MeshRenderer>().sharedMaterial = wheelDropLook;
            drop.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            drop.SetActive(false);
            wheelDrop = drop.transform;
            wheelRoot.gameObject.SetActive(false);
        }

        // A bar from black (bottom) to white (top) in vertex colours.
        private Mesh GradientBar()
        {
            var m = new Mesh { name = "Brightness Gradient" };
            owned.Add(m);
            float w = .014f, h = WheelRadius * 2f - .004f;
            m.SetVertices(new List<Vector3> { new Vector3(-w * .5f, -h * .5f, 0f), new Vector3(w * .5f, -h * .5f, 0f), new Vector3(-w * .5f, h * .5f, 0f), new Vector3(w * .5f, h * .5f, 0f) });
            m.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            m.SetColors(new List<Color> { Color.black, Color.black, Color.white, Color.white });
            m.SetTriangles(new[] { 0, 2, 3, 0, 3, 1 }, 0);
            m.RecalculateBounds();
            return m;
        }

        // The wheel's colours at the current brightness (vertex colours go in as linear, F52).
        private void PaintWheel()
        {
            if (wheelMesh == null) return;
            int count = 1 + WheelRings * WheelSegments;
            var colours = new Color[count];
            colours[0] = Lin(Color.HSVToRGB(0f, 0f, wheelValue));
            for (int r = 1; r <= WheelRings; r++)
                for (int s = 0; s < WheelSegments; s++)
                    colours[1 + (r - 1) * WheelSegments + s] = Lin(Color.HSVToRGB((float)s / WheelSegments, (float)r / WheelRings, wheelValue));
            wheelMesh.colors = colours;
            wheelShownValue = wheelValue;
        }

#if UNITY_EDITOR
        private void EditorTubeClick()
        {
            if (UnityEngine.XR.XRSettings.isDeviceActive || HandIO.Active || Camera.main == null || wheelTube == null || !wheelTube.gameObject.activeInHierarchy) return;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;
            var ray = Camera.main.ScreenPointToRay(mouse.position.ReadValue());
            if (RayDistance(ray, wheelTube.position) < .015f) ToggleWheel();
        }

        private Vector3? EditorWheelPoint(out bool clicked)
        {
            clicked = false;
            if (UnityEngine.XR.XRSettings.isDeviceActive || HandIO.Active || Camera.main == null) return null;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return null;
            var ray = Camera.main.ScreenPointToRay(mouse.position.ReadValue());
            if (!new Plane(wheelRoot.forward, wheelRoot.position).Raycast(ray, out float d)) return null;
            Vector3 p = ray.GetPoint(d);
            Vector3 local = wheelRoot.InverseTransformPoint(p);
            if (new Vector2(local.x, local.y).magnitude > WheelRadius * 1.04f && Mathf.Abs(local.x - (WheelRadius + .03f)) > .018f) return null;
            clicked = mouse.leftButton.wasPressedThisFrame;
            return p;
        }
#endif

        // ------------------------------------------------------------ tests

        public void SimulateWheelPick(Vector2 xyOnWheel, float value)
        {
            if (!WheelOpen) OpenWheel();
            wheelValue = Mathf.Clamp01(value);
            WheelColour = WheelAt(xyOnWheel, wheelValue);
            CommitWheel(WheelColour);
        }
        public Vector3 WheelPoint(Vector2 xyOnWheel) => wheelRoot != null ? wheelRoot.TransformPoint(new Vector3(xyOnWheel.x, xyOnWheel.y, 0f)) : Vector3.zero;
    }
}
