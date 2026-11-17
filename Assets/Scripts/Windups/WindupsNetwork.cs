using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace Windups
{
    // F78 airplane mode (2026-11-14): is the internet there? Checked once as the app starts (WindupsArrival.Awake).
    // No network at all (airplane mode, Wi-Fi off) is known at once from the OS; Wi-Fi without internet (a plane's or a
    // cafe's sign-in portal) is found by one short request to the AI server: any HTTP answer, even 401, means online; no
    // answer within ProbeSeconds (or a broken TLS handshake, as a portal gives) means offline. The request is
    // asynchronous, so no frame waits for it. The Editor always counts as online (other features' checks must not turn
    // into airplane mode when the PC's connection drops): preview airplane mode there with ForceOffline or the
    // EditorOfflineKey session flag.
    public static class WindupsNetwork
    {
        public const string ProbeUrl = "https://api.openai.com/v1/models";
        public const int ProbeSeconds = 3;
        public const string EditorOfflineKey = "Windups.AirplanePreview";

        public static bool Known { get; private set; }
        public static bool Started { get; private set; } // a check was started (only the maker scene checks)
        public static bool Offline { get; private set; }
        public static string Reason { get; private set; } = "";
        // Checks: behave as if there were no internet. A plain static, not Editor-only, so player code may read it
        // (F68: members declared only under UNITY_EDITOR break the Quest build).
        public static bool ForceOffline;

        public static IEnumerator Check()
        {
            Known = false;
            Started = true;
            if (ForceOffline) { Set(true, "forced (check)"); yield break; }
#if UNITY_EDITOR
            bool preview = UnityEditor.SessionState.GetBool(EditorOfflineKey, false);
            Set(preview, preview ? "Editor airplane preview" : "Editor");
            yield break;
#else
            if (Application.internetReachability == NetworkReachability.NotReachable) { Set(true, "no network"); yield break; }
            using (var http = UnityWebRequest.Head(ProbeUrl))
            {
                http.timeout = ProbeSeconds;
                yield return http.SendWebRequest();
                bool answered = http.result == UnityWebRequest.Result.Success || http.result == UnityWebRequest.Result.ProtocolError;
                if (ForceOffline) Set(true, "forced (check)");
                else Set(!answered, answered ? "server answered " + http.responseCode : "no answer: " + http.error);
            }
#endif
        }

        private static void Set(bool offline, string reason)
        {
            Offline = offline;
            Reason = reason;
            Known = true;
            Debug.Log("[Windups Network] " + (offline ? "offline" : "online") + " (" + reason + ")");
        }
    }
}
