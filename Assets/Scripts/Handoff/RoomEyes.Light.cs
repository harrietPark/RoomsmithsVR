using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Windups.Handoff
{
    // F69 (2026-11-14, the user: "colours and textures taken from my room still look very different from what I see -
    // is it my room's lighting? can that be solved?"). A picked colour was the camera picture's pixel as it is: the
    // room's light is in it twice - its colour (a warm lamp turns everything orange and takes the blue out) and its
    // level (the camera exposes for the whole view, so things come out darker than the eye, which adapts, sees them).
    // Device picks so far were all dark and warm: #916e28, #a86911, #ab683c; desk wood #9a5f11.
    //
    // Here the light is estimated from the same picture and taken out again:
    //  - The reference is what is bright and has no colour of its own in the picture (a white wall, the ceiling, paper,
    //    a white desk): the brightest tenth of the near-neutral pixels, clipped highlights (lamps, windows) left out.
    //    Its colour is the light's colour, its level the level a white thing has in this picture.
    //  - Colours are corrected in linear light: each channel divided by the light's colour (white balance), and all of
    //    them scaled so that the reference comes out as a white thing (WhiteLevel). Both are limited (GainMost,
    //    ExposureMost) and scaled by how sure the estimate is: with little neutral area in view (Confidence) the
    //    correction fades toward none - a picture of one coloured wall is left as it is rather than made grey.
    //  - Hue is kept when a channel would clip (the colour is scaled down as a whole).
    // Everything is plain arithmetic on Frame.pixels (any thread), about 7000 samples per picture, cached per frame.
    // Strength 0 switches it off (the pick is the camera pixel again). AgentScripts/VerifyRoomLight.cs.
    public sealed partial class RoomEyes
    {
        public static float LightStrength = 1f;                  // 0: no correction
        public const float WhiteLevel = .8f;                     // linear level a white thing gets (paper, a white wall)
        public const float GainMost = 1.9f, ExposureMost = 2.2f, ExposureLeast = .75f;
        public const float NeutralSaturation = .22f;             // "no colour of its own" (after the first white-balance guess)
        public const float FullConfidenceShare = .06f;           // this share of the picture near-neutral and bright: sure

        public sealed class RoomLight
        {
            public Vector3 tint = Vector3.one;   // the light's colour, linear, largest channel 1 (white light: 1,1,1)
            public float level = WhiteLevel;     // linear luminance of a white thing in this picture
            public float confidence;             // 0-1: how much bright neutral area the estimate rests on
            public Vector3 gain = Vector3.one;   // what a linear colour is multiplied by (strength and limits applied)
            public string Summary => "light tint " + tint.x.ToString("0.00") + "/" + tint.y.ToString("0.00") + "/" + tint.z.ToString("0.00") +
                                     " level " + level.ToString("0.00") + " sure " + confidence.ToString("0.00") +
                                     " gain " + gain.x.ToString("0.00") + "/" + gain.y.ToString("0.00") + "/" + gain.z.ToString("0.00");
        }

        private static readonly ConditionalWeakTable<Frame, RoomLight> lights = new ConditionalWeakTable<Frame, RoomLight>();
        private static readonly float[] toLinear = BuildToLinear();

        private static float[] BuildToLinear()
        {
            var t = new float[256];
            for (int i = 0; i < 256; i++) { float c = i / 255f; t[i] = c <= .04045f ? c / 12.92f : Mathf.Pow((c + .055f) / 1.055f, 2.4f); }
            return t;
        }

        private static float ToGamma(float l) => l <= .0031308f ? l * 12.92f : 1.055f * Mathf.Pow(l, 1f / 2.4f) - .055f;
        private static float Luma(Vector3 c) => c.x * .2126f + c.y * .7152f + c.z * .0722f;

        // The Editor's stand-in picture is a render of the scene, not a camera picture: nothing is taken out of it
        // (checks that want to see the correction on it set this).
        public static bool LightInRenderedPictures;
        private static void NoLight(Frame frame)
        {
            lock (lights) { lights.Remove(frame); lights.Add(frame, new RoomLight()); }
        }

        // The room's light in this picture (estimated once per picture).
        public static RoomLight LightOf(Frame frame)
        {
            if (frame?.pixels == null) return new RoomLight();
            lock (lights)
            {
                if (lights.TryGetValue(frame, out var known)) return known;
                var light = EstimateLight(frame.pixels, frame.size.x, frame.size.y);
                lights.Add(frame, light);
                return light;
            }
        }

        public static RoomLight EstimateLight(Color32[] pixels, int w, int h)
        {
            var light = new RoomLight();
            if (pixels == null || w < 8 || h < 8 || pixels.Length < w * h) return light;
            int step = Mathf.Max(1, Mathf.Min(w, h) / 72);
            int cols = w / step, rows = h / step, n = cols * rows;
            var lin = new Vector3[n];
            var clipped = new bool[n];
            Vector3 sum = Vector3.zero;
            int used = 0;
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < cols; i++)
                {
                    Color32 c = pixels[j * step * w + i * step];
                    int k = j * cols + i;
                    lin[k] = new Vector3(toLinear[c.r], toLinear[c.g], toLinear[c.b]);
                    clipped[k] = c.r >= 250 || c.g >= 250 || c.b >= 250; // a lamp, a window: not a surface
                    if (!clipped[k]) { sum += lin[k]; used++; }
                }
            if (used < 50) return light;

            // First guess of the light's colour: the picture's average (a room is mostly walls, floor and furniture).
            Vector3 guess = Normalised(sum / used);
            // Two rounds: neutral = little colour once the guess is divided out; the light's colour = the brightest
            // tenth of those.
            var lum = new float[n];
            var sorted = new float[n];
            Vector3 tint = guess;
            float level = 0f, share = 0f;
            for (int round = 0; round < 2; round++)
            {
                int count = 0;
                for (int k = 0; k < n; k++)
                {
                    lum[k] = -1f;
                    if (clipped[k]) continue;
                    Vector3 b = new Vector3(lin[k].x / tint.x, lin[k].y / tint.y, lin[k].z / tint.z);
                    float max = Mathf.Max(b.x, Mathf.Max(b.y, b.z)), min = Mathf.Min(b.x, Mathf.Min(b.y, b.z));
                    if (max < .02f || (max - min) / max > NeutralSaturation) continue;
                    lum[k] = Luma(lin[k]);
                    sorted[count++] = lum[k];
                }
                if (count < 20) { share = 0f; break; }
                Array.Sort(sorted, 0, count);
                float cut = sorted[Mathf.Clamp((int)(count * .9f), 0, count - 1)];
                Vector3 top = Vector3.zero;
                int tops = 0;
                for (int k = 0; k < n; k++)
                    if (lum[k] >= cut && lum[k] >= 0f) { top += lin[k]; tops++; }
                if (tops == 0) { share = 0f; break; }
                top /= tops;
                tint = Normalised(top);
                level = Luma(top) / Mathf.Max(.05f, Luma(tint)); // the white thing's level once its tint is taken out
                // Only neutral area at least half as bright as that reference supports it (a dark grey sofa does not).
                int strong = 0;
                for (int k = 0; k < n; k++) if (lum[k] >= cut * .5f) strong++;
                share = strong / (float)n;
            }

            // A room's light is warm or cool (lamp to daylight), hardly green or magenta: only that much is taken for
            // the light's colour. The rest is the walls' own colour (a green wall is not green light) and stays.
            Vector3 seenTint = tint;
            tint = Plausible(tint);
            if (level > 0f) level *= Luma(seenTint) / Mathf.Max(.05f, Luma(tint)); // the reference's luminance is as measured
            light.tint = tint;
            light.level = level > 0f ? level : WhiteLevel;
            light.confidence = Mathf.Clamp01(share / FullConfidenceShare);
            light.gain = GainFor(light, LightStrength);
            return light;
        }

        public const float WarmCoolMost = 2f, GreenLeast = .92f, GreenMost = 1.1f;

        // The nearest warm-to-cool light: red against blue within WarmCoolMost either way, green close to their middle.
        private static Vector3 Plausible(Vector3 tint)
        {
            float redBlue = Mathf.Clamp(tint.x / Mathf.Max(.02f, tint.z), 1f / WarmCoolMost, WarmCoolMost);
            float r = Mathf.Sqrt(redBlue), b = 1f / r;
            float green = Mathf.Clamp(tint.y / Mathf.Max(.02f, Mathf.Sqrt(tint.x * tint.z)), GreenLeast, GreenMost);
            return Normalised(new Vector3(r, green, b));
        }

        private static Vector3 Normalised(Vector3 c)
        {
            float max = Mathf.Max(c.x, Mathf.Max(c.y, c.z));
            return max < 1e-5f ? Vector3.one : new Vector3(Mathf.Max(.05f, c.x / max), Mathf.Max(.05f, c.y / max), Mathf.Max(.05f, c.z / max));
        }

        // What a linear colour is multiplied by: white balance (luminance kept) times exposure, limited, faded by
        // confidence and strength.
        public static Vector3 GainFor(RoomLight light, float strength)
        {
            float k = Mathf.Clamp01(strength) * light.confidence;
            if (k <= 0f) return Vector3.one;
            // The reference as pictured is tint x level. White balance makes it neutral at the same luminance
            // (each channel times Luma(tint) / tint); exposure then brings that luminance to WhiteLevel.
            float tintLuma = Luma(light.tint);
            Vector3 balance = new Vector3(Mathf.Clamp(tintLuma / light.tint.x, 1f / GainMost, GainMost),
                                          Mathf.Clamp(tintLuma / light.tint.y, 1f / GainMost, GainMost),
                                          Mathf.Clamp(tintLuma / light.tint.z, 1f / GainMost, GainMost));
            float exposure = Mathf.Clamp(WhiteLevel / Mathf.Max(.02f, light.level * tintLuma), ExposureLeast, ExposureMost);
            Vector3 full = balance * exposure;
            return Vector3.Lerp(Vector3.one, full, k);
        }

        // A colour of the picture as the thing's own colour (the room's light taken out).
        public static Color Corrected(Frame frame, Color picked) => Corrected(LightOf(frame), picked);

        public static Color Corrected(RoomLight light, Color picked)
        {
            if (light == null || light.gain == Vector3.one) return picked;
            Vector3 l = new Vector3(Mathf.GammaToLinearSpace(picked.r), Mathf.GammaToLinearSpace(picked.g), Mathf.GammaToLinearSpace(picked.b));
            l = Vector3.Scale(l, light.gain);
            float max = Mathf.Max(l.x, Mathf.Max(l.y, l.z));
            if (max > 1f) l /= max; // keep the hue
            return new Color(ToGamma(l.x), ToGamma(l.y), ToGamma(l.z), picked.a);
        }

        // The same for a block of pixels (a texture crop), in place.
        public static void Correct(RoomLight light, Color32[] pixels)
        {
            if (light == null || pixels == null || light.gain == Vector3.one) return;
            Vector3 g = light.gain;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                float r = toLinear[c.r] * g.x, gr = toLinear[c.g] * g.y, b = toLinear[c.b] * g.z;
                float max = Mathf.Max(r, Mathf.Max(gr, b));
                if (max > 1f) { r /= max; gr /= max; b /= max; }
                pixels[i] = new Color32((byte)Mathf.RoundToInt(ToGamma(r) * 255f), (byte)Mathf.RoundToInt(ToGamma(gr) * 255f), (byte)Mathf.RoundToInt(ToGamma(b) * 255f), c.a);
            }
        }

        // ColourAt with the room's light taken out (what a pick keeps).
        public static Color? TrueColourAt(Frame frame, Vector2 viewport, float radius = .012f)
        {
            var seen = ColourAt(frame, viewport, radius);
            return seen.HasValue ? Corrected(frame, seen.Value) : (Color?)null;
        }
    }
}
