using UnityEngine;

namespace Windups
{
    // Tiny synthesized toy sounds (no audio assets, no licensing): pops, paper rustles,
    // button boops, start/stop blips, a soft chime, a bonk and a whoosh. Played spatially
    // from a small pool so each sound comes from the object that made it.
    public sealed class ToySounds : MonoBehaviour
    {
        // 2026-11-15: Ratchet (one click of a wind-up key), Whirr (its spring let go), Jingle (the title's music-box run).
        public enum Cue { Pop, Paper, Press, Start, Stop, Chime, Bonk, Whoosh, Clap, Brush, Drip, Peel, Splat, Scan, Ding, Ask, Ratchet, Whirr, Jingle }
        private const int CueCount = 19;

        public static ToySounds Instance { get; private set; }
        [Range(0f, 1f)] public float volume = .7f;
        public int PlayedCount { get; private set; }
        private readonly int[] played = new int[CueCount];
        public int CountOf(Cue cue) => played[(int)cue];

        private AudioClip[] clips;
        private AudioSource[] voices;
        private readonly float[] lastPlayed = new float[CueCount];
        private int next;
        private int rate;
        private static float SampleRate = 48000f;

        private void Awake()
        {
            Instance = this;
            rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            SampleRate = rate;
            clips = new[]
            {
                Make("ToyPop", .11f, Pop), Make("ToyPaper", .22f, Paper), Make("ToyPress", .12f, Press),
                Make("ToyStart", .2f, Start), Make("ToyStop", .2f, Stop), Make("ToyChime", .9f, Chime),
                Make("ToyBonk", .2f, Bonk), Make("ToyWhoosh", .38f, Whoosh), Make("ToyClap", .2f, Clap),
                Make("ToyBrush", .2f, Brush), Make("ToyDrip", .16f, Drip), Make("ToyPeel", .36f, Peel), Make("ToySplat", .14f, Splat),
                Make("ToyScan", .5f, Scan), Make("ToyDing", .75f, Ding), Make("ToyAsk", .5f, Ask),
                Make("ToyRatchet", .12f, Ratchet), Make("ToyWhirr", .8f, Whirr), Make("ToyJingle", 1.7f, Jingle)
            };
            voices = new AudioSource[8];
            for (int i = 0; i < voices.Length; i++)
            {
                var voice = new GameObject("Toy Voice " + i).AddComponent<AudioSource>();
                voice.transform.SetParent(transform, false);
                voice.playOnAwake = false;
                voice.spatialBlend = 1f;
                voice.rolloffMode = AudioRolloffMode.Logarithmic;
                voice.minDistance = .35f;
                voice.maxDistance = 6f;
                voice.dopplerLevel = 0f;
                voices[i] = voice;
            }
            for (int i = 0; i < lastPlayed.Length; i++) lastPlayed[i] = -10f;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (clips != null) foreach (var clip in clips) if (clip != null) Destroy(clip);
        }

        // F55: the Sounds switch in Fixer's settings box (saved by MakerSession; footsteps follow it too).
        public static bool Muted;

        public static void Play(Cue cue, Vector3 position, float gain = 1f, float pitch = 1f)
        {
            if (Instance != null && !Muted) Instance.PlayAt(cue, position, gain, pitch);
        }

        private void PlayAt(Cue cue, Vector3 position, float gain, float pitch)
        {
            int index = (int)cue;
            // Several cards can change on one frame; one pop per 60 ms is enough.
            if (Time.unscaledTime - lastPlayed[index] < .06f) return;
            lastPlayed[index] = Time.unscaledTime;
            var voice = voices[next];
            next = (next + 1) % voices.Length;
            voice.transform.position = position;
            voice.pitch = pitch * Random.Range(.96f, 1.04f);
            voice.volume = Mathf.Clamp01(volume * gain);
            voice.clip = clips[index];
            voice.Play();
            PlayedCount++;
            played[index]++; // F76: checks count the cues
        }

        // Footsteps (2026-11-11): tiny pitter-patter taps, a different one per character - tik (bright),
        // tok (woody), tup (soft, low), tap, pip and pat - each in two slightly different feet.
        public enum StepVoice { Tik, Tok, Tup, Tap, Pip, Pat }
        private static readonly System.Collections.Generic.Dictionary<int, AudioClip> steps = new System.Collections.Generic.Dictionary<int, AudioClip>();
        public static AudioClip Step(StepVoice voice, int foot)
        {
            int v = (int)voice, key = v * 2 + (foot & 1);
            if (steps.TryGetValue(key, out var made) && made != null) return made;
            int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            float[] tone = { 2100f, 950f, 380f, 1300f, 1700f, 700f };
            float[] decay = { .011f, .018f, .03f, .015f, .012f, .02f };
            float[] grit = { .25f, .12f, .45f, .2f, .18f, .3f };
            float frequency = tone[v] * ((foot & 1) == 0 ? 1f : 1.07f), d = decay[v];
            int count = Mathf.CeilToInt(d * 6f * rate);
            var data = new float[count];
            float phase = 0f, low = 0f;
            var random = new System.Random(key + 11);
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)rate;
                phase += 2f * Mathf.PI * frequency * Mathf.Lerp(1.25f, 1f, Mathf.Clamp01(t / .006f)) / rate;
                float envelope = Mathf.Clamp01(t / .0015f) * Mathf.Exp(-t / d);
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);
                low += (noise - low) * .25f;
                float body = Mathf.Sin(phase) + (voice == StepVoice.Tok ? .3f * Mathf.Sin(phase * 2.03f) : 0f);
                data[i] = (body * (1f - grit[v]) + low * grit[v] * 2f) * envelope * .8f;
            }
            made = AudioClip.Create("Step" + key, count, 1, rate, false);
            made.SetData(data, 0);
            steps[key] = made;
            return made;
        }

        private delegate float Wave(float t, float duration, ref float state);

        private AudioClip Make(string name, float duration, Wave wave)
        {
            int count = Mathf.CeilToInt(duration * rate);
            var data = new float[count];
            float state = 0f;
            for (int i = 0; i < count; i++) data[i] = Mathf.Clamp(wave(i / (float)rate, duration, ref state), -1f, 1f);
            var clip = AudioClip.Create(name, count, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static float Envelope(float t, float attack, float decay) =>
            Mathf.Clamp01(t / attack) * Mathf.Exp(-t / decay);

        private static float Sine(float t, float frequency) => Mathf.Sin(2f * Mathf.PI * frequency * t);

        // Pitch-dropping bubble pop.
        private static float Pop(float t, float d, ref float phase)
        {
            float frequency = Mathf.Lerp(980f, 380f, Mathf.Sqrt(t / d));
            phase += 2f * Mathf.PI * frequency / SampleRate;
            return Mathf.Sin(phase) * Envelope(t, .003f, .028f) * .9f;
        }

        // Filtered noise with a few crinkles.
        private static float Paper(float t, float d, ref float low)
        {
            float noise = Random.value * 2f - 1f;
            low += (noise - low) * .35f;
            float crinkle = .55f + .45f * Mathf.Abs(Mathf.Sin(t * 95f) * Mathf.Sin(t * 37f));
            return (noise - low) * crinkle * Envelope(t, .01f, .07f) * .55f;
        }

        private static float Press(float t, float d, ref float phase)
        {
            float frequency = Mathf.Lerp(340f, 250f, t / d);
            phase += 2f * Mathf.PI * frequency / SampleRate;
            return Mathf.Sin(phase) * Envelope(t, .004f, .035f) * .8f;
        }

        private static float Start(float t, float d, ref float s) =>
            (t < .085f ? Sine(t, 660f) * Envelope(t, .004f, .03f) : Sine(t, 990f) * Envelope(t - .085f, .004f, .04f)) * .6f;

        private static float Stop(float t, float d, ref float s) =>
            (t < .085f ? Sine(t, 990f) * Envelope(t, .004f, .03f) : Sine(t, 660f) * Envelope(t - .085f, .004f, .04f)) * .6f;

        // Soft two-note bell (C6 then E6) with an octave partial.
        private static float Chime(float t, float d, ref float s)
        {
            float first = (Sine(t, 1046.5f) + .3f * Sine(t, 2093f)) * Envelope(t, .004f, .25f);
            float u = Mathf.Max(0f, t - .11f);
            float second = t < .11f ? 0f : (Sine(u, 1318.5f) + .3f * Sine(u, 2637f)) * Envelope(u, .004f, .3f);
            return (first + second) * .32f;
        }

        // F56: Spark's frame scanning ("ji-jing~"): a buzzy tone that slides up, with a quick tremble. Played again
        // and again while the scan runs.
        private static float Scan(float t, float d, ref float phase)
        {
            float k = t / d;
            float frequency = Mathf.Lerp(520f, 980f, k * k) * (1f + .03f * Mathf.Sin(2f * Mathf.PI * 34f * t));
            phase += 2f * Mathf.PI * frequency / SampleRate;
            float wave = Mathf.Sin(phase) * .6f + Mathf.Sign(Mathf.Sin(phase)) * .12f + Mathf.Sin(phase * 2.01f) * .2f;
            float envelope = Mathf.Clamp01(t / .02f) * Mathf.Clamp01((d - t) / .05f);
            return wave * envelope * .3f;
        }

        // F56: the scan is done ("ti-ding!"): a short high grace note, then a bright bell a fifth above.
        private static float Ding(float t, float d, ref float s)
        {
            float first = (Sine(t, 1568f) + .25f * Sine(t, 3136f)) * Envelope(t, .003f, .07f);
            float u = Mathf.Max(0f, t - .09f);
            float second = t < .09f ? 0f : (Sine(u, 2093f) + .35f * Sine(u, 4186f) + .12f * Sine(u, 6279f)) * Envelope(u, .003f, .32f);
            return (first * .8f + second) * .34f;
        }

        // F76: a request came up (reply pills, Done, Find ideas) or a teammate wants to be seen: a friendly woody
        // "boo-dee!", lower and rounder than the chime.
        private static float Ask(float t, float d, ref float s)
        {
            float first = AskNote(t, 880f);
            float u = Mathf.Max(0f, t - .14f);
            float second = t < .14f ? 0f : AskNote(u, 1174.7f) * 1.1f;
            return (first + second) * .36f;
        }

        private static float AskNote(float t, float frequency)
        {
            float x = 2f * Mathf.PI * frequency * (1f + .03f * Mathf.Exp(-t / .02f)) * t; // slides into the note
            return (Mathf.Sin(x) + .1f * Mathf.Sin(2f * x) + .22f * Mathf.Sin(3f * x)) * Envelope(t, .004f, .11f);
        }

        private static float Bonk(float t, float d, ref float phase)
        {
            float frequency = Mathf.Lerp(200f, 115f, t / d);
            phase += 2f * Mathf.PI * frequency / SampleRate;
            float wave = Mathf.Sin(phase) + .25f * Mathf.Sign(Mathf.Sin(phase));
            return wave * Envelope(t, .003f, .05f) * .6f;
        }

        // A crisp hand clap: three slaps a few milliseconds apart, then a short bright tail, all from
        // band-passed noise (about 0.9 to 2.7 kHz, where a real clap rings), with a tiny sparkle on top.
        private static float clapLow, clapPrev, clapHigh;
        private static float Clap(float t, float d, ref float s)
        {
            if (t <= 0f) clapLow = clapPrev = clapHigh = 0f;
            float noise = Random.value * 2f - 1f;
            clapLow += (noise - clapLow) * .3f;
            clapHigh = clapLow - clapPrev + .88f * clapHigh;
            clapPrev = clapLow;
            float slaps = Mathf.Exp(-t / .004f) * .75f;
            if (t >= .009f) slaps = Mathf.Max(slaps, Mathf.Exp(-(t - .009f) / .004f) * .85f);
            if (t >= .019f) slaps = Mathf.Max(slaps, Mathf.Exp(-(t - .019f) / .005f));
            float tail = t >= .019f ? Mathf.Exp(-(t - .019f) / .05f) * .45f : 0f;
            float sparkle = t >= .02f ? Sine(t, 2637f) * Envelope(t - .02f, .002f, .06f) * .06f : 0f;
            return clapHigh * (slaps + tail) * 2.2f + sparkle;
        }

        // A soft bristle swish: band-limited noise that swells and fades, with a little flicker of bristles.
        private static float brushLow, brushPrev;
        private static float Brush(float t, float d, ref float s)
        {
            if (t <= 0f) brushLow = brushPrev = 0f;
            float noise = Random.value * 2f - 1f;
            brushLow += (noise - brushLow) * .45f;
            float band = brushLow - brushPrev * .7f;
            brushPrev = brushLow;
            float swell = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / d));
            float flicker = .7f + .3f * Mathf.Abs(Mathf.Sin(t * 260f));
            return band * swell * swell * flicker * .55f;
        }

        // A liquid bloop: a pitch that rises quickly, like a drop pulling free.
        private static float Drip(float t, float d, ref float phase)
        {
            float frequency = Mathf.Lerp(320f, 1100f, Mathf.Pow(t / d, .6f));
            phase += 2f * Mathf.PI * frequency / SampleRate;
            return Mathf.Sin(phase) * Envelope(t, .004f, .045f) * .7f;
        }

        // Tape peeling off: crackles that come faster and faster.
        private static float Peel(float t, float d, ref float s)
        {
            float rate = Mathf.Lerp(40f, 260f, t / d);
            float crackle = Mathf.Pow(Mathf.Abs(Mathf.Sin(t * rate * Mathf.PI)), 18f);
            float noise = Random.value * 2f - 1f;
            return noise * (.12f + crackle * .8f) * Mathf.Sin(Mathf.PI * t / d) * .5f;
        }

        // A wet splat: a low thump with a short spray of noise.
        private static float Splat(float t, float d, ref float phase)
        {
            float frequency = Mathf.Lerp(180f, 70f, t / d);
            phase += 2f * Mathf.PI * frequency / SampleRate;
            float thump = Mathf.Sin(phase) * Envelope(t, .002f, .03f);
            float spray = (Random.value * 2f - 1f) * Envelope(t, .001f, .018f) * .6f;
            return (thump + spray) * .7f;
        }

        // 2026-11-15 (the user: "when the title comes, a wind-up key turning with its sound, and a sound for the title"):
        // one turn of a wind-up key: three tight metal clicks ("kr-r-rk"), each a tick of noise and a short ring.
        private static float Ratchet(float t, float d, ref float s)
        {
            float sum = 0f;
            for (int i = 0; i < 3; i++)
            {
                float u = t - i * .026f;
                if (u < 0f) continue;
                float tick = (Random.value * 2f - 1f) * Mathf.Exp(-u / .0018f);
                float ring = (Sine(u, 2850f + i * 140f) + .5f * Sine(u, 4300f)) * Mathf.Exp(-u / .009f);
                sum += (tick * .9f + ring * .55f) * (i == 2 ? 1f : .7f);
            }
            float body = Sine(t, 190f) * Envelope(Mathf.Max(0f, t - .052f), .002f, .02f) * (t >= .052f ? .5f : 0f); // the tray knocks back
            return (sum + body) * .55f;
        }

        // The spring let go: clicks that come fast and slow down, over a soft metal buzz that fades.
        private static float Whirr(float t, float d, ref float phase)
        {
            float k = t / d;
            float rate = Mathf.Lerp(70f, 12f, Mathf.Sqrt(k));          // clicks per second
            phase += rate / SampleRate;
            float frac = phase - Mathf.Floor(phase);
            float click = (Random.value * 2f - 1f) * Mathf.Exp(-frac / rate / .0016f) * .8f + Sine(frac / rate, 3200f) * Mathf.Exp(-frac / rate / .006f) * .35f;
            float buzz = (Sine(t, Mathf.Lerp(150f, 90f, k)) + .3f * Mathf.Sign(Sine(t, Mathf.Lerp(150f, 90f, k)))) * .12f;
            float fade = Mathf.Clamp01(t / .01f) * (1f - k) * (1f - k);
            return (click + buzz) * fade * .6f;
        }

        // The title's music-box run: C-E-G-C up the comb, then a bright "ta-da" chord with a little shimmer.
        private static readonly float[] JingleNotes = { 1046.5f, 1318.5f, 1568f, 2093f, 2637f };
        private static float Jingle(float t, float d, ref float s)
        {
            float sum = 0f;
            for (int i = 0; i < JingleNotes.Length; i++)
            {
                float u = t - i * .085f;
                if (u >= 0f) sum += Tine(u, JingleNotes[i]) * (.7f + i * .06f);
            }
            float c = t - .5f;
            if (c >= 0f)
            {
                sum += (Tine(c, 1568f) + Tine(c, 2093f) + .8f * Tine(c, 2637f)) * .7f;
                sum += Sine(c, 4186f) * Envelope(c, .002f, .12f) * (.5f + .5f * Sine(c, 23f)) * .15f; // shimmer
            }
            return sum * .26f;
        }

        // One tine of a music box: a pure note, a quick bright partial and a long soft decay.
        private static float Tine(float t, float frequency) =>
            (Sine(t, frequency) + .35f * Sine(t, frequency * 2f) * Mathf.Exp(-t / .05f) + .18f * Sine(t, frequency * 5.4f) * Mathf.Exp(-t / .012f)) * Envelope(t, .0015f, .35f);

        private static float Whoosh(float t, float d, ref float low)
        {
            float noise = Random.value * 2f - 1f;
            float cutoff = Mathf.Lerp(.05f, .3f, Mathf.Sin(Mathf.PI * t / d));
            low += (noise - low) * cutoff;
            return low * Mathf.Sin(Mathf.PI * t / d) * .9f;
        }
    }
}
