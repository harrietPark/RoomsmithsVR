using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Windups
{
    // The app's permissions (2026-11-11): the microphone, the headset camera (Spark's eyes, colours and
    // textures) and the space (the room scan) are asked together, once, when the app starts. Whatever was
    // refused is asked again the moment a feature needs it (holding the mic, Spark looking, loading the
    // room). If Android no longer shows the question ("don't ask again"), the caller tells the person to
    // allow it in the app's permissions.
    public static class WindupsPermissions
    {
        public const string Microphone = "android.permission.RECORD_AUDIO";
        public const string Camera = "horizonos.permission.HEADSET_CAMERA";
        public const string Space = "com.oculus.permission.USE_SCENE";
        public static readonly string[] All = { Space, Camera, Microphone };

        public static bool Asking { get; private set; }
        private static readonly HashSet<string> blocked = new HashSet<string>(); // "don't ask again"

        public static bool Has(string permission)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission);
#else
            return true;
#endif
        }

        public static bool Blocked(string permission) => blocked.Contains(permission);

        // Before the first scene loads: everything still missing, in one go.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AskAllAtStart()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var missing = new List<string>();
            foreach (var p in All) if (!Has(p)) missing.Add(p);
            if (missing.Count == 0) return;
            int waiting = missing.Count;
            Asking = true;
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => { if (--waiting <= 0) Asking = false; };
            callbacks.PermissionDenied += _ => { if (--waiting <= 0) Asking = false; };
            callbacks.PermissionDeniedAndDontAskAgain += p => { blocked.Add(p); if (--waiting <= 0) Asking = false; };
            UnityEngine.Android.Permission.RequestUserPermissions(missing.ToArray(), callbacks);
            Debug.Log("[Windups Permissions] Asking at start: " + string.Join(", ", missing));
#endif
        }

        // A feature needs this permission now: granted already, or ask (again) and wait for the answer.
        public static IEnumerator Ensure(string permission, Action<bool> done)
        {
            float until = Time.realtimeSinceStartup + 60f;
            while (Asking && Time.realtimeSinceStartup < until) yield return null;
            if (Has(permission)) { done?.Invoke(true); yield break; }
#if UNITY_ANDROID && !UNITY_EDITOR
            bool answered = false;
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => answered = true;
            callbacks.PermissionDenied += _ => answered = true;
            callbacks.PermissionDeniedAndDontAskAgain += p => { blocked.Add(p); answered = true; };
            Asking = true;
            UnityEngine.Android.Permission.RequestUserPermission(permission, callbacks);
            until = Time.realtimeSinceStartup + 60f;
            while (!answered && !Has(permission) && Time.realtimeSinceStartup < until) yield return null;
            Asking = false;
#endif
            done?.Invoke(Has(permission));
        }
    }
}
