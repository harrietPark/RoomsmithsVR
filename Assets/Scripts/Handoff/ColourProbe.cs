using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Windups.Handoff
{
    // F69 (2026-11-14): colour as seen.
    //  1. The display. The camera's picture is sRGB (Rec 709); the app told the headset its picture is P3
    //     (OculusRuntimeSettings colorSpace), so every colour, a picked one too, was shown more saturated than it is.
    //     On the headset the app now declares Rec 709 (SrgbDisplay; the Editor is not touched).
    //  2. A colour check that needs nobody wearing the headset (the user: "I want it to work without me putting it
    //     on"): started with the launch option `--es colourtest 1`
    //         adb shell am start -n <package>/<activity> --es colourtest 1
    //     it takes Count camera pictures Every seconds apart and writes, for each, into persistentDataPath/eyes:
    //         probe_N_raw.jpg   the camera's picture
    //         probe_N_true.jpg  the same with the room's light taken out (RoomEyes.Light.cs)
    //         probe_N.txt       the light estimate and nine sample points (camera colour -> kept colour)
    //     and shows a board half a metre ahead: a row of known colours (white, greys, red, green, blue, yellow) and
    //     the nine samples as the camera had them (upper row) and as kept (lower row). A headset screenshot taken from
    //     the PC then shows the passthrough, the known colours and the samples in one picture.
    //     Without the launch option none of this exists.
    public sealed class ColourProbe : MonoBehaviour
    {
        public static bool SrgbDisplay = true;
        public const int Count = 8;
        public const float Every = 3f, Patch = .035f;
        public static readonly Color[] Known =
        {
            Color.white, new Color(.75f, .75f, .75f), new Color(.5f, .5f, .5f), new Color(.25f, .25f, .25f),
            new Color(.8f, .2f, .2f), new Color(.2f, .65f, .3f), new Color(.2f, .35f, .8f), new Color(.95f, .8f, .2f)
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
#if !UNITY_EDITOR
            try
            {
                if (SrgbDisplay && OVRManager.instance != null)
                {
                    var before = OVRManager.instance.colorGamut;
                    OVRManager.instance.colorGamut = OVRManager.ColorSpace.Rec_709;
                    Debug.Log("[Windups Colour] display colour space " + before + " -> Rec_709 (native " + OVRManager.instance.nativeColorGamut + ")");
                }
            }
            catch (Exception exception) { Debug.LogWarning("[Windups Colour] colour space not set: " + exception.Message); }
#endif
            if (!string.IsNullOrEmpty(LaunchExtra("colourtest"))) new GameObject("Colour Probe").AddComponent<ColourProbe>();
        }

        // A string extra of the launching intent (Android), else null.
        public static string LaunchExtra(string key)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                return intent.Call<string>("getStringExtra", key);
            }
            catch (Exception) { return null; }
#else
            return TestExtra;
#endif
        }

        public static string TestExtra; // Editor checks
        public int Taken { get; private set; }
        public string Folder { get; private set; }
        private Material look;
        private Transform board;
        private readonly List<Renderer> seenPatches = new List<Renderer>(), keptPatches = new List<Renderer>();

        private IEnumerator Start()
        {
            Folder = Path.Combine(Application.persistentDataPath, "eyes");
            Debug.Log("[Windups Colour] probe started");
            yield return new WaitForSeconds(6f);
            RoomEyes eyes = null;
            for (float until = Time.time + 20f; eyes == null && Time.time < until; ) { eyes = FindFirstObjectByType<RoomEyes>(); yield return null; }
            if (eyes == null) eyes = gameObject.AddComponent<RoomEyes>();
            var head = Camera.main != null ? Camera.main.transform : transform;
            BuildBoard(head);
            for (int n = 1; n <= Count; n++)
            {
                RoomEyes.Frame frame = null;
                yield return eyes.Capture(f => frame = f);
                if (frame?.pixels == null)
                {
                    Debug.LogWarning("[Windups Colour] probe " + n + ": no picture (" + (eyes.LastError ?? "camera") + ")");
                    yield return new WaitForSeconds(Every);
                    continue;
                }
                Record(n, frame);
                Taken = n;
                yield return new WaitForSeconds(Every);
            }
            Debug.Log("[Windups Colour] probe done: " + Taken + " pictures in " + Folder);
        }

        private void Record(int n, RoomEyes.Frame frame)
        {
            var light = RoomEyes.LightOf(frame);
            var text = new StringBuilder();
            text.AppendLine(DateTime.Now.ToString("MM-dd HH:mm:ss") + " " + frame.size.x + "x" + frame.size.y);
            text.AppendLine(light.Summary);
            int k = 0;
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 3; i++, k++)
                {
                    var at = new Vector2(.25f + i * .25f, .25f + j * .25f);
                    Color seen = RoomEyes.ColourAt(frame, at) ?? Color.black, kept = RoomEyes.Corrected(light, seen);
                    text.AppendLine(at.ToString("F2") + " camera #" + ColorUtility.ToHtmlStringRGB(seen) + " -> kept #" + ColorUtility.ToHtmlStringRGB(kept));
                    Tint(seenPatches[k], seen);
                    Tint(keptPatches[k], kept);
                }
            string folder = Folder, name = "probe_" + n, words = text.ToString();
            byte[] raw = frame.jpeg;
            int w = frame.size.x, h = frame.size.y;
            var pixels = (Color32[])frame.pixels.Clone();
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, name + ".txt"), words);
                    if (raw != null) File.WriteAllBytes(Path.Combine(folder, name + "_raw.jpg"), raw);
                    RoomEyes.Correct(light, pixels);
                    File.WriteAllBytes(Path.Combine(folder, name + "_true.jpg"), RoomEyes.Jpeg(pixels, w, h, 88));
                }
                catch (Exception) { }
            });
            Debug.Log("[Windups Colour] probe " + n + ": " + light.Summary);
        }

        // Half a metre ahead, a little below the eyes: known colours on top, then the samples (camera / kept).
        private void BuildBoard(Transform head)
        {
            var shader = Shader.Find("Windups/True Colour");
            look = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Unlit")) { name = "Colour Probe" };
            if (look.HasProperty("_Shade")) look.SetFloat("_Shade", 1f);
            if (look.HasProperty("_RimStrength")) look.SetFloat("_RimStrength", 0f);
            board = new GameObject("Colour Probe Board").transform;
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            board.SetPositionAndRotation(head.position + forward * .5f - Vector3.up * .16f, Quaternion.LookRotation(forward, Vector3.up));
            float gap = Patch * 1.15f;
            for (int i = 0; i < Known.Length; i++) Tint(Quad(new Vector3((i - (Known.Length - 1) * .5f) * gap, gap, 0f)), Known[i]);
            for (int i = 0; i < 9; i++)
            {
                seenPatches.Add(Quad(new Vector3((i - 4) * gap, 0f, 0f)));
                keptPatches.Add(Quad(new Vector3((i - 4) * gap, -gap, 0f)));
            }
        }

        private Renderer Quad(Vector3 local)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(board, false);
            go.transform.localPosition = local;
            go.transform.localScale = Vector3.one * Patch;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = look;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Tint(r, Color.grey);
            return r;
        }

        private static void Tint(Renderer r, Color colour)
        {
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", colour);
            r.SetPropertyBlock(block);
        }

        private void OnDestroy()
        {
            if (look != null) Destroy(look);
            if (board != null) Destroy(board.gameObject);
        }
    }
}
