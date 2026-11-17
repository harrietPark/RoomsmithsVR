using UnityEngine;

namespace Windups
{
    // Small anti-aliased UI shapes generated once at runtime, so the toy style needs no
    // extra texture assets: a 9-sliced pill and a thin ring.
    public static class ToyUiSprites
    {
        private static Sprite pill, ring, softPill, tail;

        public static Sprite Pill => pill != null ? pill : pill = CreatePill();
        public static Sprite Ring => ring != null ? ring : ring = CreateRing();
        // A pill whose edge fades out over a few pixels: a soft drop shadow under a Pill (9-sliced).
        public static Sprite SoftPill => softPill != null ? softPill : softPill = CreateSoftPill();
        // A small rounded triangle pointing down (a speech bubble's tail), pivot at its tip.
        public static Sprite Tail => tail != null ? tail : tail = CreateTail();

        private static Sprite CreateSoftPill()
        {
            const int size = 64;
            const float radius = 31.5f, feather = 9f;
            var texture = NewTexture(size, "ToySoftPill");
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(Mathf.Abs(x + .5f - size * .5f) - (size * .5f - radius), 0f);
                float dy = Mathf.Max(Mathf.Abs(y + .5f - size * .5f) - (size * .5f - radius), 0f);
                float inside = radius - Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(inside / feather));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(31, 31, 31, 31));
        }

        private static Sprite CreateTail()
        {
            const int width = 48, height = 32;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "ToyTail",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
            var pixels = new Color32[width * height];
            // A triangle from the top edge down to the bottom middle, its corners rounded by `round`
            // (the inner triangle grown by that radius); its top runs past the texture, so it is cut flat there.
            const float round = 5f;
            var a = new Vector2(round + 1f, height + round);
            var b = new Vector2(width - round - 1f, height + round);
            var c = new Vector2(width * .5f, round + 1f);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var p = new Vector2(x + .5f, y + .5f);
                float d = Mathf.Min(SegmentDistance(p, a, b), Mathf.Min(SegmentDistance(p, b, c), SegmentDistance(p, c, a)));
                if (Inside(p, a, b, c)) d = -d;
                float alpha = Mathf.Clamp01(round - d + .5f);
                pixels[y * width + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(.5f, 0f), 100f);
        }

        private static Sprite CreatePill()
        {
            const int size = 64;
            const float radius = 31.5f;
            var texture = NewTexture(size, "ToyPill");
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(Mathf.Abs(x + .5f - size * .5f) - (size * .5f - radius), 0f);
                float dy = Mathf.Max(Mathf.Abs(y + .5f - size * .5f) - (size * .5f - radius), 0f);
                float alpha = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + .5f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(31, 31, 31, 31));
        }

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return Vector2.Distance(p, a + ab * t);
        }

        private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float Cross(Vector2 o, Vector2 u, Vector2 v) => (u.x - o.x) * (v.y - o.y) - (u.y - o.y) * (v.x - o.x);
            float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
            bool negative = d1 < 0f || d2 < 0f || d3 < 0f, positive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(negative && positive);
        }

        private static Sprite CreateRing()
        {
            const int size = 128;
            const float outer = 63f, thickness = 7f;
            var texture = NewTexture(size, "ToyRing");
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(size * .5f, size * .5f));
                float alpha = Mathf.Clamp01(outer - d + .5f) * Mathf.Clamp01(d - (outer - thickness) + .5f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f);
        }

        private static Texture2D NewTexture(int size, string name)
        {
            return new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
        }
    }
}
