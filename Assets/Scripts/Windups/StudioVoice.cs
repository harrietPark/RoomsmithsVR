using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Windups
{
    // F25: the three studio characters speak their lines aloud (OpenAI text-to-speech), from
    // where they stand (3D audio on the character). One voice at a time; clips are cached so a
    // line replayed from its mark on the card starts at once.
    public sealed class StudioVoice : MonoBehaviour
    {
        public const string Endpoint = "https://api.openai.com/v1/audio/speech";
        public const int SampleRate = 24000;

        public bool Enabled { get; set; } = true;
        public bool IsSpeaking => current != null && current.isPlaying;
        public bool Loading { get; private set; }
        public string LastError { get; private set; }
        public int Spoken { get; private set; }
        public float LastLatency { get; private set; }

        private readonly Dictionary<string, AudioClip> cache = new Dictionary<string, AudioClip>();
        private readonly Dictionary<Transform, AudioSource> sources = new Dictionary<Transform, AudioSource>();
        private AudioSource current;
        // Every voice in the scene, so a character's body knows when its own line is playing (Talk_Loop).
        private static readonly List<StudioVoice> voices = new List<StudioVoice>();
        private void OnEnable() { if (!voices.Contains(this)) voices.Add(this); }
        private void OnDisable() => voices.Remove(this);
        public static bool IsSpeakingFrom(Transform speaker)
        {
            foreach (var v in voices)
                if (v != null && v.current != null && v.current.isPlaying && v.current.transform == speaker) return true;
            return false;
        }
        private Coroutine pending;
        private UnityWebRequest request;
        private int ticket;

        // Speaks `text` from `speaker`. `started` runs when the audio actually starts (or right
        // away when the voice is off or fails, so captions never wait on audio).
        public void Speak(Role role, Transform speaker, string text, Action<float> started = null)
        {
            Stop();
            int mine = ++ticket;
            if (!Enabled || speaker == null || string.IsNullOrWhiteSpace(text)) { started?.Invoke(0f); return; }
            pending = StartCoroutine(Run(role, speaker, text.Trim(), mine, started));
        }

        public void Stop()
        {
            ticket++;
            if (pending != null) StopCoroutine(pending);
            pending = null;
            request?.Abort();
            request = null;
            Loading = false;
            if (current != null) current.Stop();
        }

        private IEnumerator Run(Role role, Transform speaker, string text, int mine, Action<float> started)
        {
            string key = (int)role + "|" + text;
            if (!cache.TryGetValue(key, out var clip))
            {
                if (!WindupsDirectCredentials.TryGetKey(out string apiKey)) { LastError = "No key"; started?.Invoke(0f); yield break; }
                Loading = true;
                float sentAt = Time.realtimeSinceStartup;
                using (var http = new UnityWebRequest(Endpoint, "POST"))
                {
                    http.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(StudioRoles.SpeechRequest(role, text)));
                    http.downloadHandler = new DownloadHandlerBuffer();
                    http.SetRequestHeader("Content-Type", "application/json");
                    http.SetRequestHeader("Authorization", "Bearer " + apiKey);
                    http.timeout = 15;
                    request = http;
                    yield return http.SendWebRequest();
                    request = null;
                    Loading = false;
                    if (this == null || mine != ticket) yield break;
                    if (http.result != UnityWebRequest.Result.Success)
                    {
                        LastError = "Voice failed (" + http.responseCode + ")";
                        Debug.LogWarning("[Windups Studio] " + LastError + ": " + http.error);
                        started?.Invoke(0f);
                        yield break;
                    }
                    clip = FromPcm(http.downloadHandler.data, key);
                    LastLatency = Time.realtimeSinceStartup - sentAt;
                }
                if (clip == null) { LastError = "Empty voice"; started?.Invoke(0f); yield break; }
                cache[key] = clip;
            }
            LastError = null;
            current = Source(speaker);
            current.clip = clip;
            current.pitch = role == Role.Dreamer ? 1.06f : 1f;
            current.Play();
            Spoken++;
            started?.Invoke(clip.length);
            pending = null;
        }

        private AudioSource Source(Transform speaker)
        {
            if (sources.TryGetValue(speaker, out var source) && source != null) return source;
            source = speaker.gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = .4f;
            source.maxDistance = 6f;
            source.dopplerLevel = 0f;
            source.volume = 1f;
            sources[speaker] = source;
            return source;
        }

        // 24 kHz, 16-bit little-endian, mono.
        public static AudioClip FromPcm(byte[] data, string name)
        {
            if (data == null || data.Length < 4) return null;
            int count = data.Length / 2;
            var samples = new float[count];
            for (int i = 0; i < count; i++) samples[i] = (short)(data[i * 2] | (data[i * 2 + 1] << 8)) / 32768f;
            var clip = AudioClip.Create("Studio voice " + name.GetHashCode(), count, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private void OnDestroy()
        {
            request?.Abort();
            foreach (var clip in cache.Values) if (clip != null) Destroy(clip);
        }
    }
}
