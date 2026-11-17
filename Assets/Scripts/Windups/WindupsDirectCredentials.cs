using System;
using System.IO;
using UnityEngine;

namespace Windups
{
    // Only populated on the build pipeline's scene copy after an explicit local opt-in.
    // This is a test-build credential, not encryption or a safe public-distribution scheme.
    // OpenAI (required), Meshy (optional, F26) and the PC's local address for print files (F26).
    public sealed class WindupsDirectCredentials : MonoBehaviour
    {
        [SerializeField, HideInInspector] private string testApiKey;
        [SerializeField, HideInInspector] private string testMeshyKey;
        [SerializeField, HideInInspector] private string testMetaKey;
        [SerializeField, HideInInspector] private string pcUrl;

        public static bool TryGetKey(out string key)
        {
#if UNITY_EDITOR
            key = ReadLocalKey();
#else
            var credentials = FindFirstObjectByType<WindupsDirectCredentials>();
            key = credentials != null ? credentials.testApiKey : null;
#endif
            return !string.IsNullOrWhiteSpace(key);
        }

        public static bool TryGetMeshyKey(out string key)
        {
#if UNITY_EDITOR
            key = ReadLocalMeshyKey();
#else
            var credentials = FindFirstObjectByType<WindupsDirectCredentials>();
            key = credentials != null ? credentials.testMeshyKey : null;
#endif
            return !string.IsNullOrWhiteSpace(key);
        }

        // F83: Meta Model API (Segment Anything), optional: without it a model keeps its top, side and bottom parts.
        public static bool TryGetMetaKey(out string key)
        {
#if UNITY_EDITOR
            key = ReadLocalMetaKey();
#else
            var credentials = FindFirstObjectByType<WindupsDirectCredentials>();
            key = credentials != null ? credentials.testMetaKey : null;
#endif
            return !string.IsNullOrWhiteSpace(key);
        }

        // Where the PC's Windups Lab server listens (for print files), e.g. http://192.168.0.12:5177.
        public static string PcUrl
        {
            get
            {
#if UNITY_EDITOR
                return "http://localhost:5177";
#else
                var credentials = FindFirstObjectByType<WindupsDirectCredentials>();
                return credentials != null ? credentials.pcUrl : null;
#endif
            }
        }

#if UNITY_EDITOR
        public void SetBuildKey(string key) => testApiKey = key;
        public void SetMeshyKey(string key) => testMeshyKey = key;
        public void SetPcUrl(string url) => pcUrl = url;
        public void SetMetaKey(string key) => testMetaKey = key;

        public static string ReadLocalMetaKey() => ReadSetting("meta-api-key.txt", "META_API_KEY");
        public static string ReadLocalKey() => ReadSetting("openai-api-key.txt", "OPENAI_API_KEY");
        public static string ReadLocalMeshyKey() => ReadSetting("meshy-api-key.txt", "MESHY_API_KEY");

        private static string ReadSetting(string file, string envName)
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            string local = Path.Combine(root, "LocalSettings", file);
            if (File.Exists(local)) return File.ReadAllText(local).Trim();
            string env = Path.Combine(root, "Server/.env");
            if (!File.Exists(env)) return null;
            foreach (string line in File.ReadLines(env))
            {
                string value = line.Trim();
                if (!value.StartsWith(envName + "=", StringComparison.Ordinal)) continue;
                return value.Substring(envName.Length + 1).Trim().Trim('"', '\'');
            }
            return null;
        }
#endif
    }
}
