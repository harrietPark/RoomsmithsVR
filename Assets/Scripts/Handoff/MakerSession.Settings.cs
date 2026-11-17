using System;
using System.Collections.Generic;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F55 (2026-11-13): Fixer (the black one) keeps the settings. It holds up a toy gear on a little handle
    // (MakerSession.CrewTools.cs shows it near a hand, in the introduction and in the roll call); a tap on the gear,
    // or saying "settings", opens a small toy box in front of Fixer, world-locked and facing the person once:
    //   Voice (the crew's spoken voice, WindupsArrival.crewVoice) | Sounds (ToySounds and footsteps) | Tips again
    //   Start over (tap twice) | Quit (tap twice) | Close
    // Buttons are pressed by a fingertip, a pinch on them or aimed from afar, or an Editor click. The box closes
    // with Close, the gear, "close", SettingsIdle seconds untouched, or when Fixer is picked up. Voice and Sounds are
    // saved (PlayerPrefs). Language and music are not here (the user left language out; the app has no music).
    public sealed partial class MakerSession
    {
        public const string VoiceSettingKey = "thinkmade.crewVoice", SoundsSettingKey = "thinkmade.sounds";
        public static readonly string[] TipKeys =
        {
            "thinkmade.introSeen", "thinkmade.shapeInputsTaught", "thinkmade.colourInputsTaught", "thinkmade.paletteTaught",
            "thinkmade.textureTaught", "thinkmade.tapeTaught", "thinkmade.finishTaught", "thinkmade.draftTaught",
            ToolsTaughtKey, DrawHintKey, LookHintKey, SketchThumbTaughtKey
        };
        public static float SettingsIdle = 25f, ConfirmSeconds = 3.5f;
        public const float GearRadius = .016f;
        private static readonly Vector3 GearHandle = new Vector3(0f, -.036f, 0f);
        private static readonly Color GearYellow = new Color(1f, .8f, .16f), GearDark = new Color(.78f, .56f, .08f),
            GearInk = new Color(.17f, .19f, .23f), ButtonOn = new Color(.62f, .9f, .66f), ButtonOff = new Color(.86f, .87f, .86f),
            ButtonPlain = new Color(.98f, .95f, .86f), ButtonWarn = new Color(1f, .62f, .55f);

        public bool SettingsOpen => settingsBox != null && settingsBox.gameObject.activeSelf;
        public Transform SettingsBox => settingsBox;
        public int SettingsOpens { get; private set; }
        public int SettingsPresses { get; private set; }
        public int QuitRequests { get; private set; }
        public string LastSetting { get; private set; } = "";

        private Transform gear, gearHead, gearHand, settingsBox;
        private Vector3 gearPalmLocal;
        private float gearLift, settingsUsedAt, confirmUntil, settingsOpenedAt;
        private string confirming;
        private bool settingsLoaded;
        private readonly List<SettingsCap> settingsCaps = new List<SettingsCap>();
        private readonly HashSet<Hand> settingsTouchDown = new HashSet<Hand>();

        private sealed class SettingsCap
        {
            public string id;
            public Rect rect;
            public NotebookButton3D button;
            public TextMeshProUGUI label;
            public MeshFilter capFilter;
            public Color side;
            public float w, h;
            public Color colour;
        }

        // ------------------------------------------------------------ saved settings

        private void LoadSettings()
        {
            if (settingsLoaded || arrival == null) return;
            settingsLoaded = true;
            if (PlayerPrefs.HasKey(VoiceSettingKey)) arrival.crewVoice = PlayerPrefs.GetInt(VoiceSettingKey) == 1;
            ToySounds.Muted = PlayerPrefs.GetInt(SoundsSettingKey, 1) == 0;
        }

        // ------------------------------------------------------------ Fixer's gear

        private void BuildGear()
        {
            gear = new GameObject("Settings Gear").transform;
            gear.SetParent(transform, false);
            gearHead = new GameObject("Gear").transform;
            gearHead.SetParent(gear, false);
            var teeth = GearMesh(8, GearRadius, GearRadius * .74f, .005f);
            owned.Add(teeth);
            var head = Part("Gear teeth", teeth);
            head.SetParent(gearHead, false);
            var hubMesh = ToyMeshes.Disc(.0052f, .0068f, GearInk, GearInk, "GearHub");
            owned.Add(hubMesh);
            var hub = Part("Gear hub", hubMesh);
            hub.SetParent(gearHead, false);
            var dotMesh = ToyMeshes.Disc(.0019f, .0074f, GearYellow, GearYellow, "GearHubDot");
            owned.Add(dotMesh);
            var dot = Part("Gear hub dot", dotMesh);
            dot.SetParent(gearHead, false);
            var collarMesh = ToyMeshes.Disc(.0042f, .005f, GearInk, GearInk, "GearCollar");
            owned.Add(collarMesh);
            var collar = Part("Gear collar", collarMesh);
            collar.SetParent(gear, false);
            collar.localPosition = new Vector3(0f, -.0185f, 0f);
            collar.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var handleMesh = ToyMeshes.Disc(.0037f, .027f, GearYellow, GearDark, "GearHandle");
            owned.Add(handleMesh);
            var handle = Part("Gear handle", handleMesh);
            handle.SetParent(gear, false);
            handle.localPosition = new Vector3(0f, -.033f, 0f);
            handle.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            foreach (var r in gear.GetComponentsInChildren<MeshRenderer>(true)) r.receiveShadows = false;
            gear.gameObject.AddComponent<NotebookPropFollower>();
            gear.gameObject.SetActive(false);
        }

        // A flat toothed wheel in the XY plane (faces +-Z), coloured by vertex like the other toy props.
        private static Mesh GearMesh(int teethCount, float outer, float root, float thickness)
        {
            var outline = new List<Vector2>();
            for (int i = 0; i < teethCount; i++)
            {
                float a = i * Mathf.PI * 2f / teethCount, step = Mathf.PI * 2f / teethCount;
                foreach (var (angle, r) in new[] { (a, root), (a + step * .14f, outer), (a + step * .44f, outer), (a + step * .58f, root) })
                    outline.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r);
            }
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var c = new List<Color>();
            var tris = new List<int>();
            float half = thickness * .5f;
            for (int face = 0; face < 2; face++)
            {
                float z = face == 0 ? -half : half;
                int centre = v.Count;
                v.Add(new Vector3(0f, 0f, z)); n.Add(face == 0 ? Vector3.back : Vector3.forward); c.Add(GearYellow);
                for (int i = 0; i < outline.Count; i++) { v.Add(new Vector3(outline[i].x, outline[i].y, z)); n.Add(face == 0 ? Vector3.back : Vector3.forward); c.Add(GearYellow); }
                for (int i = 0; i < outline.Count; i++)
                {
                    int a = centre + 1 + i, b = centre + 1 + (i + 1) % outline.Count;
                    if (face == 0) tris.AddRange(new[] { centre, b, a }); else tris.AddRange(new[] { centre, a, b });
                }
            }
            for (int i = 0; i < outline.Count; i++)
            {
                Vector2 p = outline[i], q = outline[(i + 1) % outline.Count];
                Vector3 normal = new Vector3(q.y - p.y, p.x - q.x, 0f).normalized;
                int s = v.Count;
                v.Add(new Vector3(p.x, p.y, -half)); v.Add(new Vector3(q.x, q.y, -half)); v.Add(new Vector3(q.x, q.y, half)); v.Add(new Vector3(p.x, p.y, half));
                for (int k = 0; k < 4; k++) { n.Add(normal); c.Add(GearDark); }
                tris.AddRange(new[] { s, s + 2, s + 1, s, s + 3, s + 2 });
            }
            var mesh = new Mesh { name = "Settings gear" };
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetColors(c); mesh.SetTriangles(tris, 0); mesh.RecalculateBounds();
            return mesh;
        }

        // Fixer holds the gear up in its right mitten, its face toward the person, turning slowly (like Spark's
        // magnifier, MakerSession.Magnifier.cs: the mitten within HandReach, the wheel clear of the head).
        private void GearFrame()
        {
            if (gear == null || !cast.TryGetValue(Crew.Fixer, out var fixer) || fixer == null) return;
            var body = Body(Crew.Fixer);
            var hand = body != null ? body.RigHand(true) : null;
            var pet = fixer.movement;
            bool want = ToolShown(Crew.Fixer) && hand != null && pet != null && !pet.IsHeld;
            if (pet == null || pet.IsHeld) gearLift = 0f;
            gearLift = Mathf.MoveTowards(gearLift, want ? 1f : 0f, Time.deltaTime * 4f);
            if (gearLift <= 0f)
            {
                if (gear.gameObject.activeSelf)
                {
                    if (pet != null && !pet.IsHeld) ToyFx.Puff(gear.position, .006f, 2);
                    gear.gameObject.SetActive(false);
                    body?.Hold(true, null);
                }
                return;
            }
            if (!gear.gameObject.activeSelf) { gear.gameObject.SetActive(true); ToySounds.Play(ToySounds.Cue.Pop, fixer.transform.position + Vector3.up * .1f, .3f, 1.9f); }
            CacheGearPalm(body, hand);
            Vector3 origin = fixer.transform.position;
            Vector3 forward = Vector3.ProjectOnPlane(body.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .5f) forward = Vector3.forward;
            Vector3 offset = Vector3.ClampMagnitude(forward * .046f + Vector3.Cross(Vector3.up, forward) * .024f, HandReach - .005f);
            Vector3 grip = origin + offset + Vector3.up * Mathf.Lerp(.042f, .07f, gearLift);
            Vector3 toEye = Head != null ? Head.position - (grip - GearHandle) : forward;
            Quaternion turn = Quaternion.LookRotation(Vector3.ProjectOnPlane(toEye, Vector3.up).sqrMagnitude > 1e-6f ? Vector3.ProjectOnPlane(toEye, Vector3.up).normalized : forward, Vector3.up) * Quaternion.Euler(-12f, 0f, 0f);
            for (int step = 0; step < 9; step++)
            {
                Vector3 wheel = grip - turn * GearHandle;
                float clear = body.HeadDepth(wheel);
                for (int i = 0; i < 8; i++) { float a = i * Mathf.PI / 4f; clear = Mathf.Min(clear, body.HeadDepth(wheel + turn * new Vector3(Mathf.Cos(a) * GearRadius, Mathf.Sin(a) * GearRadius, 0f))); }
                if (clear >= 1.12f) break;
                grip.y = Mathf.Max(origin.y + .028f, grip.y - .004f);
            }
            Vector3 wrist = grip - turn * Vector3.Scale(gearPalmLocal, hand.lossyScale);
            body.Hold(true, new Pose(wrist, turn));
            var follower = gear.GetComponent<NotebookPropFollower>();
            follower.hand = hand; follower.handLocal = gearPalmLocal; follower.rotationFrame = null; follower.rotation = turn; follower.gripLocal = GearHandle;
            gear.localScale = Vector3.one * Mathf.SmoothStep(.2f, 1f, gearLift);
            gearHead.localRotation = Quaternion.Euler(0f, 0f, Time.time * (SettingsOpen ? 70f : 28f));
        }

        private void CacheGearPalm(WindupBody body, Transform hand)
        {
            if (gearHand == hand) return;
            gearHand = hand;
            foreach (var skin in body.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (skin.name != "Hand_R") continue;
                var mesh = new Mesh();
                skin.BakeMesh(mesh, true);
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                foreach (var vertex in mesh.vertices)
                {
                    var p = hand.InverseTransformPoint(skin.transform.TransformPoint(vertex));
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                if (mesh.vertexCount > 0) gearPalmLocal = (min + max) * .5f;
                Destroy(mesh);
                break;
            }
        }

        // ------------------------------------------------------------ the settings box

        public void OpenSettings(string why)
        {
            EnsureToolProps();
            if (!cast.TryGetValue(Crew.Fixer, out var fixer) || fixer == null || material == null) return;
            if (settingsBox == null) BuildSettingsBox();
            RefreshSettingsLabels();
            Vector3 feet = fixer.transform.position;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - feet, Vector3.up) : Vector3.back;
            toUser = toUser.sqrMagnitude > 1e-6f ? toUser.normalized : Vector3.back;
            Vector3 at = feet + toUser * .15f + Vector3.up * .11f;
            settingsBox.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at));
            settingsBox.localScale = Vector3.one * .3f;
            settingsBox.gameObject.SetActive(true);
            settingsOpenedAt = settingsUsedAt = Time.time;
            confirming = null;
            settingsTouchDown.Clear();
            SettingsOpens++;
            LastSetting = "open (" + why + ")";
            WriteInputLog("settings open (" + why + ")");
            ShowToolFor(Crew.Fixer, 1.5f);
            Body(Crew.Fixer)?.PlaySoon("React_Nod", .15f);
            ToySounds.Play(ToySounds.Cue.Pop, at, .45f, 1.5f);
            ToyFx.Puff(at, .008f, 3);
        }

        public void CloseSettings(string why)
        {
            if (!SettingsOpen) return;
            settingsBox.gameObject.SetActive(false);
            confirming = null;
            LastSetting = "close (" + why + ")";
            WriteInputLog("settings close (" + why + ")");
            ToyFx.Puff(settingsBox.position, .006f, 2);
            ToySounds.Play(ToySounds.Cue.Paper, settingsBox.position, .2f, 1.8f);
        }

        private const float BoxW = .2f, BoxH = .096f, CapW = .058f, CapH = .022f;

        private void BuildSettingsBox()
        {
            settingsBox = new GameObject("Settings Box").transform;
            settingsBox.SetParent(transform, false);
            Color edge = MakerRoles.Color(Crew.Fixer);
            var panelMesh = ToyMeshes.RoundedSlab(BoxW, BoxH, .012f, .006f, new Color(.99f, .97f, .9f), edge, new Color(.99f, .97f, .9f), "SettingsPanel");
            owned.Add(panelMesh);
            var panel = Part("Settings panel", panelMesh);
            panel.SetParent(settingsBox, false);
            panel.localPosition = new Vector3(0f, 0f, .002f);
            var titleCanvas = SettingsCanvas("Settings title", settingsBox, BoxW * 1000f, 14f, new Vector3(0f, BoxH * .5f - .011f, -.0012f));
            var title = HatIdeaCard.Label(titleCanvas, font, 6f, 0f, BoxW * 1000f - 12f, 14f, 8f, 11f, GearInk, FontStyles.Normal, TextAlignmentOptions.Left);
            title.text = "Settings";
            float row1 = .006f, row2 = -.025f, dx = CapW + .006f;
            SettingsButton("voice", -dx, row1, () => ToggleVoice());
            SettingsButton("sounds", 0f, row1, () => ToggleSounds());
            SettingsButton("tips", dx, row1, () => ResetTips());
            SettingsButton("start over", -dx * .5f, row2, () => Confirm("start over", () => { CloseSettings("start over"); StartOver(); }));
            SettingsButton("quit", dx * .5f, row2, () => Confirm("quit", QuitApp));
            // F79 (Quest: "Close would be better as an X in the top right"): a small round-cornered X in the corner.
            SettingsButton("close", BoxW * .5f - CloseCap * .5f - .006f, BoxH * .5f - CloseCap * .5f - .006f, () => CloseSettings("button"), CloseCap, CloseCap);
            var closeCap = settingsCaps[settingsCaps.Count - 1].button.cap;
            Color ink = GearInk;
            var bar = ToyMeshes.RoundedSlab(.0018f, .011f, .0008f, .0006f, ink, ink, ink, "SettingsCloseBar");
            owned.Add(bar);
            for (int i = 0; i < 2; i++)
            {
                var b = Part("X", bar);
                b.SetParent(closeCap, false);
                b.localPosition = new Vector3(0f, 0f, -.0004f);
                b.localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 45f : -45f);
            }
            settingsBox.gameObject.SetActive(false);
        }

        public const float CloseCap = .02f;
        public int LessonsReplayed { get; private set; }
        public const string ReplayLessonsLabel = "Replay lessons";

        private RectTransform SettingsCanvas(string name, Transform parent, float width, float height, Vector3 at)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
            r.sizeDelta = new Vector2(width, height);
            r.localScale = Vector3.one * .001f;
            r.localPosition = at;
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            return r;
        }

        private void SettingsButton(string id, float x, float y, Action click, float w = CapW, float h = CapH)
        {
            var root = new GameObject("Settings " + id).transform;
            root.SetParent(settingsBox, false);
            root.localPosition = new Vector3(x, y, -.006f);
            var button = root.gameObject.AddComponent<NotebookButton3D>();
            button.interactable = true;
            var cap = new SettingsCap { id = id, rect = new Rect(x - w * .5f, y - h * .5f, w, h), button = button, side = new Color(.34f, .36f, .42f), w = w, h = h };
            button.clicked = () => { SettingsPresses++; settingsUsedAt = Time.time; LastSetting = id; WriteInputLog("settings press " + id); click(); RefreshSettingsLabels(); };
            button.footMesh = ToyMeshes.RoundedSlab(w + .002f, h + .002f, .005f, .003f, cap.side, cap.side * .85f, cap.side, "Settings socket");
            var foot = Part("Button socket", button.footMesh);
            foot.SetParent(root, false);
            foot.localPosition = Vector3.forward * .003f;
            cap.colour = ButtonPlain;
            button.capMesh = ToyMeshes.RoundedSlab(w, h, .0055f, .003f, cap.colour, cap.side, cap.side, "Settings cap");
            button.cap = Part("Raised cap", button.capMesh);
            button.cap.SetParent(root, false);
            cap.capFilter = button.cap.GetComponent<MeshFilter>();
            var canvas = SettingsCanvas("Button label", button.cap, w * 1000f, h * 1000f, new Vector3(0f, 0f, -.0003f));
            cap.label = HatIdeaCard.Label(canvas, font, 2f, 0f, w * 1000f - 4f, h * 1000f, 6f, 9f, GearInk, FontStyles.Normal, TextAlignmentOptions.Center);
            settingsCaps.Add(cap);
        }

        private void RefreshSettingsLabels()
        {
            if (settingsBox == null) return;
            if (confirming != null && Time.time > confirmUntil) confirming = null;
            foreach (var cap in settingsCaps)
            {
                string text;
                Color colour = ButtonPlain;
                switch (cap.id)
                {
                    case "voice": bool voiced = arrival != null && arrival.crewVoice; text = voiced ? "Voice: On" : "Voice: Off"; colour = voiced ? ButtonOn : ButtonOff; break;
                    case "sounds": text = ToySounds.Muted ? "Sounds: Off" : "Sounds: On"; colour = ToySounds.Muted ? ButtonOff : ButtonOn; break;
                    case "tips": text = ReplayLessonsLabel; break; // F79: was "Tips again"
                    case "close": text = ""; break;                // F79: the X in the corner
                    case "start over": text = confirming == "start over" ? "Sure? Tap again" : NewCreationLabel; if (confirming == "start over") colour = ButtonWarn; break; // F77: "New creation" (id kept)
                    case "quit": text = confirming == "quit" ? "Sure? Tap again" : "Quit"; if (confirming == "quit") colour = ButtonWarn; break;
                    default: text = "Close"; break;
                }
                if (cap.label.text != text) cap.label.text = text;
                if (cap.colour != colour)
                {
                    cap.colour = colour;
                    var old = cap.capFilter.sharedMesh;
                    cap.button.capMesh = ToyMeshes.RoundedSlab(cap.w, cap.h, .0055f, .003f, colour, cap.side, cap.side, "Settings cap");
                    cap.capFilter.sharedMesh = cap.button.capMesh;
                    if (old != null) Destroy(old);
                }
            }
        }

        private void Confirm(string id, Action act)
        {
            if (confirming == id && Time.time <= confirmUntil) { confirming = null; act(); return; }
            confirming = id;
            confirmUntil = Time.time + ConfirmSeconds;
        }

        private void ToggleVoice()
        {
            if (arrival == null) return;
            arrival.crewVoice = !arrival.crewVoice;
            PlayerPrefs.SetInt(VoiceSettingKey, arrival.crewVoice ? 1 : 0);
            PlayerPrefs.Save();
            if (!arrival.crewVoice) { Voice?.Stop(); Ack(Crew.Fixer, "Voices off. Cards only."); }
            else Say(Crew.Fixer, "Voices on!");
        }

        private void ToggleSounds()
        {
            ToySounds.Muted = !ToySounds.Muted;
            PlayerPrefs.SetInt(SoundsSettingKey, ToySounds.Muted ? 0 : 1);
            PlayerPrefs.Save();
            if (!ToySounds.Muted && settingsBox != null) ToySounds.Play(ToySounds.Cue.Chime, settingsBox.position, .4f, 1.4f);
        }

        private void ResetTips()
        {
            foreach (var key in TipKeys) PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
            Say(Crew.Fixer, "Okay! I'll show you how again."); // F79: what Replay lessons did, said
            LessonsReplayed++;
        }

        private void QuitApp()
        {
            QuitRequests++;
            WriteInputLog("quit");
            CloseSettings("quit");
#if UNITY_EDITOR
            Debug.Log("[Windups Settings] Quit pressed (the Editor keeps playing; the headset app closes).");
#else
            Application.Quit();
#endif
        }

        // ------------------------------------------------------------ pressing the buttons

        private void SettingsFrame()
        {
            if (!SettingsOpen) return;
            float age = Time.time - settingsOpenedAt;
            settingsBox.localScale = Vector3.one * (age < .25f ? Mathf.Lerp(.3f, 1f, Mathf.SmoothStep(0f, 1f, age / .25f)) : 1f);
            if (Time.time - settingsUsedAt > SettingsIdle) { CloseSettings("idle"); return; }
            if (cast.TryGetValue(Crew.Fixer, out var fixer) && fixer != null && fixer.movement != null && fixer.movement.IsHeld) { CloseSettings("Fixer picked up"); return; }
            if (confirming != null && Time.time > confirmUntil) RefreshSettingsLabels();
            if (age < .3f) return;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out var tip))
                {
                    settingsPress.Forget(hand);
                    continue;
                }
                var p = settingsBox.InverseTransformPoint(tip.position);
                SettingsCap touched = null;
                if (p.z >= -.016f && p.z <= .008f)
                    foreach (var cap in settingsCaps) if (cap.rect.Contains(new Vector2(p.x, p.y))) { touched = cap; break; }
                if (settingsPress.Step(this, hand, touched != null)) { PressLog("settings button", hand); touched.button.Press(); }
            }
        }

        private readonly FingerPress settingsPress = new FingerPress(); // F60

        // A pinch on a button, or aimed at one; an Editor click (point at infinity).
        private bool SettingsPinch(Vector3 point, Ray? ray)
        {
            if (!SettingsOpen || Time.time - settingsOpenedAt < .3f) return false;
            Vector2? hit = null;
            var near = settingsBox.InverseTransformPoint(point);
            if (!float.IsInfinity(point.x) && Mathf.Abs(near.z) < .02f) hit = new Vector2(near.x, near.y);
            else if (ray.HasValue)
            {
                var plane = new Plane(settingsBox.forward, settingsBox.position);
                if (plane.Raycast(ray.Value, out float d))
                {
                    var p = settingsBox.InverseTransformPoint(ray.Value.GetPoint(d));
                    hit = new Vector2(p.x, p.y);
                }
            }
            if (!hit.HasValue) return false;
            foreach (var cap in settingsCaps)
                if (cap.rect.Contains(hit.Value)) { cap.button.Press(); return true; }
            // Anywhere else on the box: taken (nothing behind it is picked through it).
            return Mathf.Abs(hit.Value.x) <= BoxW * .5f && Mathf.Abs(hit.Value.y) <= BoxH * .5f;
        }

        // Tests: a button as a fingertip would press it.
        public void SimulateSettingsPress(string id)
        {
            foreach (var cap in settingsCaps) if (cap.id == id) { cap.button.Press(); return; }
        }

        // Tests: where a fingertip presses that button (just in front of its cap), and what it says now.
        public Vector3 SettingsButtonPoint(string id)
        {
            foreach (var cap in settingsCaps) if (cap.id == id) return settingsBox.TransformPoint(new Vector3(cap.rect.center.x, cap.rect.center.y, -.008f));
            return Vector3.zero;
        }

        public string SettingsLabel(string id)
        {
            foreach (var cap in settingsCaps) if (cap.id == id) return cap.label.text;
            return null;
        }

        // "settings" / "close settings" (MakerSession.cs Route).
        private bool SettingsVoice(string p)
        {
            if (p == "settings" || p.Contains("open settings") || p.Contains("show settings") || p.Contains("the settings") || p == "options" || p.Contains("설정"))
            {
                if (SettingsOpen && (p.Contains("close") || p.Contains("닫"))) { CloseSettings("voice"); return true; }
                if (!SettingsOpen) OpenSettings("voice");
                return true;
            }
            if (SettingsOpen && (p == "close" || p.Contains("close it") || p.Contains("close settings") || p.Contains("닫아")))
            {
                CloseSettings("voice");
                return true;
            }
            return false;
        }
    }
}
