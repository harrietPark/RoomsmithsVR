using UnityEngine;
using UnityEngine.UI;

namespace Windups
{
    // Chunky toy push-to-talk puck behind the existing uGUI button. Input stays on the
    // button's Image (transparent) so Poke/Ray and PressAndHoldMicrophone are unchanged;
    // this component only draws the puck, the glyph, the listening ring and press motion.
    public sealed class ToyMicrophoneButton : MonoBehaviour
    {
        public static readonly Color Coral = new Color(.94f, .45f, .37f, 1f);
        public static readonly Color Cream = new Color(1f, .99f, .97f, 1f);

        // Canvas units (the MicButton canvas is scaled 0.0008 m per unit): ~4.8 cm puck.
        const float Radius = 60f, Depth = 30f, Bevel = 13f, BackBevel = 6f;
        const int Segments = 48;

        public float PressAmount { get; private set; }
        public bool Listening { get; private set; }

        private WindupsMicrophone microphone;
        private RawImage glyph;
        private Image ring;
        private Transform puck;
        private Mesh mesh;
        private Color[] colors;
        private int capVertexCount;
        private bool capIsCoral;
        private float failureSeen;
        private bool wasHeld, failureSounded;
        // The designed microphone (Resources/WindupsMic, from Assets/Art/WindupsCharacter/Props/Mic.fbx):
        // when present it replaces the procedural puck. Its button sinks 1.5 mm while pressed; its light
        // and icon glow while recording (pulsing with the voice) and blink while connecting.
        // 2.2 since 2026-11-11 (the Quest try: too small to press comfortably).
        public const float MicScale = 2.2f, ButtonTravel = .002f;
        private Transform mic, micButton;
        private Vector3 micButtonRest;
        private Material micLight, micIcon;
        private static readonly Color MicGlow = new Color(6f, 2.605f, .411f, 1f);
        private Color lightOff, iconOff;
        private static readonly Color LitBase = new Color(1f, .851f, .627f, 1f);

        // F46: the mic's body and top button in the model's own space (from its meshes), so a fingertip press is
        // measured against the mic itself. (The press used to be a ball round the canvas, the middle of the body,
        // 3.9 cm below the real button on top.)
        private Bounds pressBox;
        public bool HasModel => mic != null;
        // 0..1 how close a fingertip is (set by WindupsArrival each frame): the light glows a little before a press.
        public float Hover { get; set; }
        private int busySeen;

        public void Initialize(WindupsMicrophone owner, RawImage icon, Material material)
        {
            microphone = owner;
            glyph = icon;
            failureSeen = owner != null ? owner.LastFailureTime : 0f;
            var model = Resources.Load<GameObject>("WindupsMic");
            if (model != null) BuildMic(model);
            else BuildPuck(material);
            var ringObject = new GameObject("Listening Ring", typeof(RectTransform), typeof(Image));
            ring = ringObject.GetComponent<Image>();
            ring.rectTransform.SetParent(transform, false);
            ring.rectTransform.sizeDelta = new Vector2(Radius * 2f + 18f, Radius * 2f + 18f);
            ring.rectTransform.localPosition = new Vector3(0f, 0f, -.5f);
            ring.sprite = ToyUiSprites.Ring;
            ring.color = new Color(Coral.r, Coral.g, Coral.b, 0f);
            ring.raycastTarget = false;
            ring.transform.SetAsFirstSibling();
            if (glyph != null)
            {
                glyph.color = Coral;
                glyph.rectTransform.sizeDelta *= .72f;
                glyph.transform.SetAsLastSibling();
            }
            // The model has its own icon and light: no flat glyph or ring over it.
            if (mic != null)
            {
                if (glyph != null) glyph.enabled = false;
                ring.enabled = false;
            }
        }

        private void BuildMic(GameObject model)
        {
            mic = Instantiate(model).transform;
            mic.name = "Windups Mic";
            mic.SetParent(transform, true);
            mic.localScale = Vector3.one * MicScale / Mathf.Max(.0001f, transform.lossyScale.x);
            foreach (var r in mic.GetComponentsInChildren<Renderer>(true))
            {
                // Drawn like the crew (Windups/Toy Character, AgentScripts/ConfigureMicLook.cs): no shadows on it
                // or from it, no scene lighting.
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                if (r.name.EndsWith("Button")) { micButton = r.transform; micButtonRest = micButton.localPosition; }
                else if (r.name.EndsWith("Light")) { micLight = r.material; lightOff = micLight.GetColor("_BaseColor"); }
                else if (r.name.EndsWith("Icon")) { micIcon = r.material; iconOff = micIcon.GetColor("_BaseColor"); }
            }
            bool any = false;
            foreach (var filter in mic.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                var b = filter.sharedMesh.bounds;
                var toMic = mic.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var corner = toMic.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { pressBox = new Bounds(corner, Vector3.zero); any = true; }
                    else pressBox.Encapsulate(corner);
                }
            }
            // F103: the button itself (its own mesh), in the mic's space.
            var buttonFilter = micButton != null ? micButton.GetComponent<MeshFilter>() : null;
            if (buttonFilter != null && buttonFilter.sharedMesh != null)
            {
                var b = buttonFilter.sharedMesh.bounds;
                var toMic = mic.worldToLocalMatrix * buttonFilter.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var corner = toMic.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (i == 0) buttonBox = new Bounds(corner, Vector3.zero);
                    else buttonBox.Encapsulate(corner);
                }
                hasButtonBox = true;
            }
            busySeen = microphone != null ? microphone.BusyPresses : 0;
            PlaceMic();
        }

        // F103 (Quest 2026-11-16, the user: "make the mic record only when I press its button; everything else is grab and
        // release"): the press is measured against the button's own mesh, not the top 30 % of the whole mic. `over`: the
        // point is over the button (its footprint, ButtonEdge round it) and no lower than ButtonBelow under its top - a
        // fingertip pressing it goes into the virtual cap. Metres outside the button, 0 inside it.
        private Bounds buttonBox;
        private bool hasButtonBox;
        public const float ButtonEdge = .005f, ButtonBelow = .02f;
        public bool HasButton => mic != null && hasButtonBox;
        public float ButtonDistance(Vector3 point, out bool over)
        {
            if (!HasButton) return PressDistance(point, out over);
            Vector3 local = mic.InverseTransformPoint(point);
            float scale = Mathf.Max(1e-4f, mic.lossyScale.x);
            float edge = ButtonEdge / scale;
            over = local.x >= buttonBox.min.x - edge && local.x <= buttonBox.max.x + edge &&
                   local.z >= buttonBox.min.z - edge && local.z <= buttonBox.max.z + edge &&
                   local.y >= buttonBox.max.y - ButtonBelow / scale;
            return Vector3.Distance(local, buttonBox.ClosestPoint(local)) * scale;
        }

        // The middle of the button's top, in the world (checks, the glow).
        public Vector3 ButtonCap => HasButton ? mic.TransformPoint(new Vector3(buttonBox.center.x, buttonBox.max.y, buttonBox.center.z)) : ButtonTop;

        // How far (metres) a point is outside the mic, 0 inside it; `topOrFront` when it is over the top (the
        // button) or in front (the icon): the two sides a press comes from, not the sides or back a pinch to pick
        // the mic up usually takes.
        public float PressDistance(Vector3 point, out bool topOrFront)
        {
            if (mic == null)
            {
                topOrFront = true;
                return Vector3.Distance(point, transform.position);
            }
            Vector3 local = mic.InverseTransformPoint(point);
            // F66 (the user: "I want to move the mic but its button keeps getting pressed"): only the top is the
            // button. The front (the icon) lies where a hand reaches to pinch the body, so it no longer presses.
            // F67 (Quest: "the record button barely presses"): a fingertip goes into a virtual mic, so the 8 mm band
            // at the very top was crossed in a frame or two. The button is the top TopShare of the mic's height, for
            // a point over its top (within its footprint, TopEdge round it); a hand at the sides still grabs.
            float scale = Mathf.Max(1e-4f, mic.lossyScale.x);
            float edge = TopEdge / scale;
            bool overTop = local.x >= pressBox.min.x - edge && local.x <= pressBox.max.x + edge && local.z >= pressBox.min.z - edge && local.z <= pressBox.max.z + edge;
            topOrFront = overTop && local.y >= pressBox.max.y - pressBox.size.y * TopShare;
            return Vector3.Distance(local, pressBox.ClosestPoint(local)) * scale;
        }

        public const float TopShare = .3f, TopEdge = .006f; // the top 30 % of the mic, over its top (6 mm round it)

        // The point of the mic nearest to `point` (the mint dot where a pinch would pick it up).
        public Vector3 ClosestPoint(Vector3 point) => mic == null ? transform.position : mic.TransformPoint(pressBox.ClosestPoint(mic.InverseTransformPoint(point)));

        // The middle of the top of the mic (its button), in the world.
        public Vector3 ButtonTop => mic != null ? mic.TransformPoint(new Vector3(pressBox.center.x, pressBox.max.y, pressBox.center.z)) : transform.position;

        // Upright on its base, its front (+Z) the way the mic's canvas faces. The canvas is turned toward the
        // person only when the mic is put somewhere (WindupsArrival: placed, moved after a walk, dropped), so
        // the mic never turns with the person's head or gaze (2026-11-12: it used to face the camera every frame).
        private void PlaceMic()
        {
            if (mic == null) return;
            Vector3 front = Vector3.ProjectOnPlane(-transform.forward, Vector3.up);
            if (front.sqrMagnitude < .0001f) front = Vector3.ProjectOnPlane(transform.up, Vector3.up);
            if (front.sqrMagnitude < .0001f) front = Vector3.forward;
            mic.SetPositionAndRotation(transform.position - Vector3.up * .018f * MicScale, Quaternion.LookRotation(front.normalized, Vector3.up));
        }

        private void SetGlow(Material m, Color off, float glow)
        {
            if (m == null) return;
            m.SetColor("_BaseColor", Color.Lerp(off, LitBase, Mathf.Clamp01(glow * 2f)));
            if (glow > .001f) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", MicGlow * glow); }
            else { m.DisableKeyword("_EMISSION"); m.SetColor("_EmissionColor", Color.black); }
        }

        private void BuildPuck(Material material)
        {
            var puckObject = new GameObject("Toy Microphone Puck", typeof(MeshFilter), typeof(MeshRenderer));
            puck = puckObject.transform;
            puck.SetParent(transform, false);
            puck.localPosition = new Vector3(0f, 0f, 2f);
            var renderer = puckObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            // Lathe profile (radius, depth) with normals; +Z points away from the viewer.
            var profile = new System.Collections.Generic.List<Vector4>();
            profile.Add(new Vector4(0f, 0f, 0f, -1f));
            for (int i = 0; i <= 5; i++)
            {
                float phi = i / 5f * Mathf.PI * .5f;
                float s = Mathf.Sin(phi), c = Mathf.Cos(phi);
                profile.Add(new Vector4(Radius - Bevel + Bevel * s, Bevel - Bevel * c, s, -c));
            }
            for (int i = 0; i <= 3; i++)
            {
                float phi = i / 3f * Mathf.PI * .5f;
                float s = Mathf.Sin(phi), c = Mathf.Cos(phi);
                profile.Add(new Vector4(Radius - BackBevel + BackBevel * c, Depth - BackBevel + BackBevel * s, c, s));
            }
            profile.Add(new Vector4(0f, Depth, 0f, 1f));
            int rings = profile.Count, columns = Segments + 1;
            var vertices = new Vector3[rings * columns];
            var normals = new Vector3[vertices.Length];
            colors = new Color[vertices.Length];
            // The flat face plus the first half of the front bevel read as the cap.
            capVertexCount = 4 * columns;
            for (int m = 0; m < rings; m++)
            for (int s = 0; s < columns; s++)
            {
                float angle = s / (float)Segments * Mathf.PI * 2f;
                float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                var p = profile[m];
                int index = m * columns + s;
                vertices[index] = new Vector3(p.x * cos, p.x * sin, p.y);
                normals[index] = new Vector3(p.z * cos, p.z * sin, p.w).normalized;
                colors[index] = index < capVertexCount ? Cream : Coral;
            }
            var triangles = new int[(rings - 1) * Segments * 6];
            int t = 0;
            for (int m = 0; m < rings - 1; m++)
            for (int s = 0; s < Segments; s++)
            {
                int a = m * columns + s, b = a + 1, c = a + columns, d = c + 1;
                triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
                triangles[t++] = b; triangles[t++] = d; triangles[t++] = c;
            }
            mesh = new Mesh { name = "ToyMicrophonePuck_Runtime" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            puckObject.GetComponent<MeshFilter>().sharedMesh = mesh;
        }

        private void SetCap(bool coral)
        {
            if (coral == capIsCoral || mesh == null) return;
            capIsCoral = coral;
            for (int i = 0; i < capVertexCount; i++) colors[i] = coral ? Coral : Cream;
            mesh.colors = colors;
        }

        private void Update()
        {
            if (microphone == null || (puck == null && mic == null)) return;
            float dt = Time.unscaledDeltaTime;
            bool wasListening = Listening;
            Listening = microphone.IsRecordingAudio;
            bool busy = (microphone.IsConnecting && !Listening) || microphone.IsStopping;
            bool held = microphone.IsHolding || Listening;
            Vector3 here = transform.position;
            if (held && !wasHeld) ToySounds.Play(ToySounds.Cue.Press, here, .7f);
            if (Listening && !wasListening) ToySounds.Play(ToySounds.Cue.Start, here, .6f);
            if (!Listening && wasListening) ToySounds.Play(ToySounds.Cue.Stop, here, .6f);
            // Pressed while the last words are still being written down: a soft, low pop (the pill says "One sec…").
            if (microphone.BusyPresses > busySeen) { busySeen = microphone.BusyPresses; ToySounds.Play(ToySounds.Cue.Pop, here, .35f, .7f); }
            wasHeld = held;
            // Let go but still within the release grace: the button comes half up (put the finger back to go on).
            PressAmount = Mathf.MoveTowards(PressAmount, microphone.IsReleasing ? .45f : held ? 1f : 0f, dt * 7f);

            float time = Time.unscaledTime;
            float breath = held || busy ? 0f : Mathf.Sin(time * 2.2f) * .018f;
            float squash = Mathf.SmoothStep(0f, 1f, PressAmount);
            float shake = 0f;
            if (microphone.LastFailureTime > failureSeen)
            {
                float since = time - microphone.LastFailureTime;
                if (!failureSounded) { ToySounds.Play(ToySounds.Cue.Bonk, here, .7f); failureSounded = true; }
                if (since < .45f) shake = Mathf.Sin(since * 60f) * (1f - since / .45f) * 7f;
                else { failureSeen = microphone.LastFailureTime; failureSounded = false; }
            }
            WindupBody.UserTalking = Listening || microphone.IsHolding;
            if (mic != null)
            {
                PlaceMic();
                mic.position += mic.right * shake * .0006f;
                if (micButton != null) micButton.localPosition = micButtonRest - new Vector3(0f, 0f, ButtonTravel * squash);
                float glow = Listening ? .55f + .45f * Mathf.Clamp01(microphone.InputLevel * 2f) + .1f * Mathf.Sin(time * 6f)
                    : busy ? .5f + .5f * Mathf.Sin(time * 9f) : Mathf.Clamp01(Hover) * .2f;
                if (Listening && microphone.IsReleasing) glow *= .5f;
                SetGlow(micLight, lightOff, glow);
                SetGlow(micIcon, iconOff, glow);
                return;
            }
            puck.localScale = new Vector3(1f + breath + squash * .03f, 1f + breath + squash * .03f, 1f - squash * .3f);
            puck.localPosition = new Vector3(shake, 0f, 2f + squash * 6f);
            SetCap(Listening);

            if (glyph != null)
            {
                Color tint = Listening ? Cream : Coral;
                if (busy) tint.a = .45f + .55f * (.5f + .5f * Mathf.Sin(time * 7f));
                glyph.color = tint;
                glyph.rectTransform.localPosition = new Vector3(shake, 0f, squash * 6f - .5f);
            }
            if (ring != null)
            {
                float level = Listening ? microphone.InputLevel : 0f;
                float cycle = Mathf.Repeat(time * .9f, 1f);
                float scale = Listening ? 1f + .12f * cycle + .22f * level : 1f;
                ring.rectTransform.localScale = Vector3.one * scale;
                ring.color = new Color(Coral.r, Coral.g, Coral.b, Listening ? (1f - cycle) * .55f + level * .45f : 0f);
            }
        }

        private void OnDestroy()
        {
            if (mesh != null) Destroy(mesh);
            if (micLight != null) Destroy(micLight);
            if (micIcon != null) Destroy(micIcon);
            WindupBody.UserTalking = false;
        }
    }
}
