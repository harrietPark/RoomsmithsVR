using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    // F76 (the user: "even without speech, a soft mumble when a bubble comes - different for each character, fitting
    // its personality, not annoying - here are the files"): every bubble (and every bubble-less reaction) plays a short
    // mumble from the speaker: random syllables cut from the user's own recordings (Resources/Mumble/<name>/, cut and
    // levelled offline by the session's cut_mumble.py: Maker 12, Fixer 19, Spark 10 pieces), about MumblePerWord a word
    // up to MumbleLongest, each syllable a little higher or lower, a question ends higher. Quiet (MumbleVolume x the
    // character's own level), from the character, never two at once, stopped when the person talks; off with Sounds
    // (ToySounds.Muted) and replaced by the real voice when the crew voice is on.
    public sealed partial class MakerSession
    {
        public static bool Mumbling = true;
        public const float MumbleVolume = .3f, MumblePerWord = .16f, MumbleLongest = 2.4f, MumbleShortest = .4f;
        // Spark's recording is the quietest once levelled (its peaks were already near full scale).
        private static readonly float[] MumbleLevel = { 1.6f, 1f, 1.15f }; // Spark, Maker, Fixer

        public int Mumbles { get; private set; }
        public bool MumblePlaying => mumble != null;
        public Crew MumbleSpeaker { get; private set; }
        public int MumblePieces(Crew crew) => MumbleClips(crew)?.Length ?? 0;

        private readonly Dictionary<Crew, AudioClip[]> mumbleClips = new Dictionary<Crew, AudioClip[]>();
        private readonly Dictionary<Crew, AudioSource> mumbleVoices = new Dictionary<Crew, AudioSource>();
        private Coroutine mumble;
        private string lastMumbled;
        private float lastMumbledAt = -10f;

        // `brief`: a reaction (a few syllables); `again`: even if the same words were just mumbled.
        private void MumbleFor(Crew crew, string text, bool brief = false, bool again = false)
        {
            if (!Mumbling || ToySounds.Muted || Voiced || WindupBody.UserTalking || string.IsNullOrWhiteSpace(text)) return;
            if (!again && text == lastMumbled && Time.time - lastMumbledAt < 4f) return; // a question shown again
            var clips = MumbleClips(crew);
            var voice = MumbleVoice(crew);
            if (clips == null || clips.Length == 0 || voice == null) return;
            lastMumbled = text;
            lastMumbledAt = Time.time;
            StopMumble();
            float total = brief ? MumbleShortest : Mathf.Clamp(MakerRoles.Words(text) * MumblePerWord, MumbleShortest, MumbleLongest);
            Mumbles++;
            MumbleSpeaker = crew;
            mumble = StartCoroutine(MumbleRun(voice, clips, total, MumbleVolume * MumbleLevel[(int)crew], text.TrimEnd().EndsWith("?")));
        }

        private IEnumerator MumbleRun(AudioSource voice, AudioClip[] clips, float total, float volume, bool asks)
        {
            float end = Time.time + total;
            int last = -1;
            while (voice != null && Time.time < end)
            {
                int k = Random.Range(0, clips.Length);
                if (k == last && clips.Length > 1) k = (k + 1) % clips.Length;
                last = k;
                var clip = clips[k];
                bool final = Time.time + clip.length >= end;
                voice.pitch = Random.Range(.94f, 1.06f) * (asks && final ? 1.12f : 1f);
                voice.volume = Mathf.Clamp01(volume);
                voice.clip = clip;
                voice.Play();
                yield return new WaitForSeconds(clip.length / voice.pitch + Random.Range(.015f, .05f));
            }
            mumble = null;
        }

        // F79: a line the arrival shows itself (the introduction, the first question) mumbles in its speaker's voice.
        public void MumbleLine(string characterName, string text)
        {
            if (!MakerRoles.TryForCharacter(characterName, out var crew)) return;
            EnsureCrew();
            MumbleFor(crew, text);
        }

        public void StopMumble()
        {
            if (mumble != null) StopCoroutine(mumble);
            mumble = null;
            foreach (var v in mumbleVoices.Values) if (v != null && v.isPlaying) v.Stop();
        }

        private AudioClip[] MumbleClips(Crew crew)
        {
            if (!mumbleClips.TryGetValue(crew, out var clips))
                mumbleClips[crew] = clips = Resources.LoadAll<AudioClip>("Mumble/" + MakerRoles.Name(crew));
            return clips;
        }

        private AudioSource MumbleVoice(Crew crew)
        {
            if (mumbleVoices.TryGetValue(crew, out var voice) && voice != null) return voice;
            if (!cast.TryGetValue(crew, out var c) || c == null) return null;
            voice = new GameObject("Mumble " + MakerRoles.Name(crew)).AddComponent<AudioSource>();
            voice.transform.SetParent(c.transform, false);
            voice.transform.localPosition = Vector3.up * .12f;
            voice.playOnAwake = false;
            voice.spatialBlend = 1f;
            voice.rolloffMode = AudioRolloffMode.Logarithmic;
            voice.minDistance = .35f;
            voice.maxDistance = 5f;
            voice.dopplerLevel = 0f;
            mumbleVoices[crew] = voice;
            return voice;
        }
    }
}
