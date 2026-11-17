using UnityEngine;

namespace Windups.Handoff
{
    // A seamless material tile from a picture of a surface (2026-11-11: "take the texture, not a photo"):
    //  - only the main material: the picture's dominant colour region (things lying on it, like a phone on a
    //    wooden desk, are left out) and the largest square inside it, resampled to 256 px;
    //  - lighting flattened: each pixel is divided by a heavily blurred copy (the light falling across the
    //    surface) and multiplied by the average colour, so a shadow, a glare or a gradient does not end up in
    //    the material (most of the way, so the surface keeps a little depth);
    //  - made seamless: blended with a copy shifted by half a tile near the edges (left/right, then
    //    top/bottom), so it repeats with no visible seams.
    public static class MaterialTile
    {
        public const int Size = 256;

        // findMain: look for the main material's square (a camera photo); off for a picture that is already
        // all material (the image model's cleaned texture), which is then only tiled. flatten: how much of the
        // light is taken out.
        // F52 `matchTo`: a tile whose colour and contrast this one keeps (the image model's cleaned texture is matched
        // to the photo's own tile: the model tends to shift the colour and darken or brighten it).
        public static Texture2D From(Texture2D picture, int size = Size, bool findMain = true, float flatten = .85f, Texture2D matchTo = null)
        {
            if (picture == null) return null;
            int w = picture.width, h = picture.height, side = Mathf.Min(w, h);
            if (side < 8) return null;
            var all = picture.GetPixels();
            int sx = (w - side) / 2, sy = (h - side) / 2;
            if (findMain) MainSquare(all, w, h, out sx, out sy, out side);
            LastMainSquare = new RectInt(sx, sy, side, side);
            var source = picture.GetPixels(sx, sy, side, side);
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    pixels[y * size + x] = Sample(source, side, (x + .5f) / size * side - .5f, (y + .5f) / size * side - .5f);

            // Flatten the light.
            var light = Blur(Blur(pixels, size, size / 6), size, size / 6);
            Color mean = Color.black;
            foreach (var c in pixels) mean += c;
            mean /= pixels.Length;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color l = light[i], c = pixels[i];
                var flat = new Color(c.r / Mathf.Max(.04f, l.r) * mean.r, c.g / Mathf.Max(.04f, l.g) * mean.g, c.b / Mathf.Max(.04f, l.b) * mean.b);
                pixels[i] = Color.Lerp(c, flat, flatten);
                pixels[i].a = 1f;
            }

            if (matchTo != null && matchTo.isReadable) MatchColour(pixels, matchTo.GetPixels32());

            // Seamless: left/right first, then top/bottom.
            pixels = Seamless(pixels, size, true);
            pixels = Seamless(pixels, size, false);
            var tile = new Texture2D(size, size, TextureFormat.RGB24, true)
            {
                name = "Material Tile", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 2
            };
            tile.SetPixels(pixels);
            tile.Apply(true);
            return tile;
        }

        public static RectInt LastMainSquare { get; private set; }

        // The largest square of the picture made of its main material. The main material is the colour most
        // of the picture has (the median); pixels close to it (within a few spreads), with small gaps closed
        // (wood grain, weave), count. If even the best square is small, the middle square is used.
        public static void MainSquare(Color[] pixels, int w, int h, out int x, out int y, out int side)
        {
            const int grid = 48;
            int gw = grid, gh = Mathf.Max(4, Mathf.RoundToInt(grid * h / (float)w));
            if (h > w) { gh = grid; gw = Mathf.Max(4, Mathf.RoundToInt(grid * w / (float)h)); }
            var cells = new Color[gw * gh];
            for (int j = 0; j < gh; j++)
                for (int i = 0; i < gw; i++)
                {
                    int px = Mathf.Min(w - 1, (int)((i + .5f) * w / gw)), py = Mathf.Min(h - 1, (int)((j + .5f) * h / gh));
                    cells[j * gw + i] = pixels[py * w + px];
                }
            float M(System.Func<Color, float> f) { var v = new float[cells.Length]; for (int k = 0; k < v.Length; k++) v[k] = f(cells[k]); System.Array.Sort(v); return v[v.Length / 2]; }
            var main = new Color(M(c => c.r), M(c => c.g), M(c => c.b));
            var d = new float[cells.Length];
            for (int k = 0; k < d.Length; k++) d[k] = Mathf.Abs(cells[k].r - main.r) + Mathf.Abs(cells[k].g - main.g) + Mathf.Abs(cells[k].b - main.b);
            var sorted = (float[])d.Clone();
            System.Array.Sort(sorted);
            float limit = Mathf.Max(.12f, sorted[sorted.Length / 2] * 3f);
            var mask = new bool[cells.Length];
            for (int k = 0; k < mask.Length; k++) mask[k] = d[k] <= limit;
            mask = Grow(Grow(mask, gw, gh, true), gw, gh, false);   // close small gaps
            // Largest all-main square (dynamic programming on the grid).
            var size = new int[cells.Length];
            int best = 0, bi = 0, bj = 0;
            for (int j = 0; j < gh; j++)
                for (int i = 0; i < gw; i++)
                {
                    int k = j * gw + i;
                    if (!mask[k]) continue;
                    size[k] = i == 0 || j == 0 ? 1 : 1 + Mathf.Min(size[k - 1], Mathf.Min(size[k - gw], size[k - gw - 1]));
                    if (size[k] > best) { best = size[k]; bi = i; bj = j; }
                }
            int whole = Mathf.Min(w, h);
            if (best < Mathf.Min(gw, gh) * .35f) { side = whole; x = (w - side) / 2; y = (h - side) / 2; return; }
            float cell = w / (float)gw;
            side = Mathf.Clamp(Mathf.FloorToInt(best * cell), 8, whole);
            x = Mathf.Clamp(Mathf.FloorToInt((bi - best + 1) * cell), 0, w - side);
            y = Mathf.Clamp(Mathf.FloorToInt((bj - best + 1) * (h / (float)gh)), 0, h - side);
        }

        // One step of dilation (true) or erosion (false) on the grid mask.
        private static bool[] Grow(bool[] mask, int gw, int gh, bool dilate)
        {
            var result = new bool[mask.Length];
            for (int j = 0; j < gh; j++)
                for (int i = 0; i < gw; i++)
                {
                    bool any = false, all = true;
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int ii = Mathf.Clamp(i + di, 0, gw - 1), jj = Mathf.Clamp(j + dj, 0, gh - 1);
                            bool m = mask[jj * gw + ii];
                            any |= m;
                            all &= m;
                        }
                    result[j * gw + i] = dilate ? any : all;
                }
            return result;
        }

        // Each channel's mean and spread moved to the reference's (the spread change kept within 0.6-1.6x).
        public static void MatchColour(Color[] pixels, Color32[] reference)
        {
            if (pixels == null || reference == null || pixels.Length == 0 || reference.Length == 0) return;
            for (int ch = 0; ch < 3; ch++)
            {
                double sum = 0, sq = 0, rsum = 0, rsq = 0;
                foreach (var c in pixels) { float v = c[ch]; sum += v; sq += v * v; }
                foreach (var c in reference) { float v = (ch == 0 ? c.r : ch == 1 ? c.g : c.b) / 255f; rsum += v; rsq += v * v; }
                float mean = (float)(sum / pixels.Length), rmean = (float)(rsum / reference.Length);
                float spread = Mathf.Sqrt(Mathf.Max(0f, (float)(sq / pixels.Length) - mean * mean));
                float rspread = Mathf.Sqrt(Mathf.Max(0f, (float)(rsq / reference.Length) - rmean * rmean));
                float gain = spread > .002f ? Mathf.Clamp(rspread / spread, .6f, 1.6f) : 1f;
                for (int i = 0; i < pixels.Length; i++)
                {
                    var c = pixels[i];
                    c[ch] = Mathf.Clamp01((c[ch] - mean) * gain + rmean);
                    pixels[i] = c;
                }
            }
        }

        public static byte[] Jpeg(byte[] picture, int size = Size, bool findMain = true, float flatten = .85f, Texture2D matchTo = null)
        {
            if (picture == null) return null;
            var source = new Texture2D(2, 2);
            if (!source.LoadImage(picture)) { Object.Destroy(source); return null; }
            var tile = From(source, size, findMain, flatten, matchTo);
            Object.Destroy(source);
            if (tile == null) return null;
            var jpeg = tile.EncodeToJPG(90);
            Object.Destroy(tile);
            return jpeg;
        }

        private static Color Sample(Color[] source, int side, float x, float y)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, side - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, side - 1);
            int x1 = Mathf.Min(side - 1, x0 + 1), y1 = Mathf.Min(side - 1, y0 + 1);
            float fx = Mathf.Clamp01(x - x0), fy = Mathf.Clamp01(y - y0);
            Color a = Color.Lerp(source[y0 * side + x0], source[y0 * side + x1], fx);
            Color b = Color.Lerp(source[y1 * side + x0], source[y1 * side + x1], fx);
            return Color.Lerp(a, b, fy);
        }

        // A box blur (rows, then columns), clamped at the edges.
        private static Color[] Blur(Color[] pixels, int size, int radius)
        {
            var rows = new Color[pixels.Length];
            var result = new Color[pixels.Length];
            float span = radius * 2 + 1;
            for (int y = 0; y < size; y++)
            {
                Color sum = Color.black;
                for (int k = -radius; k <= radius; k++) sum += pixels[y * size + Mathf.Clamp(k, 0, size - 1)];
                for (int x = 0; x < size; x++)
                {
                    rows[y * size + x] = sum / span;
                    sum += pixels[y * size + Mathf.Min(size - 1, x + radius + 1)] - pixels[y * size + Mathf.Max(0, x - radius)];
                }
            }
            for (int x = 0; x < size; x++)
            {
                Color sum = Color.black;
                for (int k = -radius; k <= radius; k++) sum += rows[Mathf.Clamp(k, 0, size - 1) * size + x];
                for (int y = 0; y < size; y++)
                {
                    result[y * size + x] = sum / span;
                    sum += rows[Mathf.Min(size - 1, y + radius + 1) * size + x] - rows[Mathf.Max(0, y - radius) * size + x];
                }
            }
            return result;
        }

        // Near the edges the tile becomes its half-shifted copy, whose own seam lies in the middle where
        // the original is kept: across the edge the pixels then continue into each other.
        private static Color[] Seamless(Color[] pixels, int size, bool across)
        {
            var result = new Color[pixels.Length];
            int half = size / 2;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int along = across ? x : y;
                    float edge = Mathf.Min(along, size - 1 - along) / (float)half;   // 0 at the edge, 1 in the middle
                    float t = Mathf.Clamp01(edge / .6f);
                    float weight = 1f - t * t * (3f - 2f * t);
                    int sx = across ? (x + half) % size : x, sy = across ? y : (y + half) % size;
                    result[y * size + x] = Color.Lerp(pixels[y * size + x], pixels[sy * size + sx], weight);
                }
            return result;
        }

        // Relief for a tile (so a material ball looks like the stone, wood or weave it came from): a height map
        // (what stands out from its surroundings, as bright), a normal map from that height, and an occlusion
        // map that darkens the crevices. All wrap around, like the tile.
        // F57 (2026-11-13, the user: "whatever material it takes it's a bit bumpy - isn't it usually smooth?"): the
        // height used to be divided by its own spread, so every picture got the same full relief - a glossy mug's
        // reflections, a printed pattern's colour edges, camera noise and JPEG blocks all became bumps. Now the height
        // keeps its real size (a fixed scale), colour edges (print, pattern) are left out, the finest noise is blurred
        // away, and `amount` says how rough the surface really is (0 = smooth: no relief at all). The work on pixels
        // is plain C# (ReliefData), so it can run off the main thread; ReliefTextures makes the textures.
        public sealed class Maps { public Texture2D normal, occlusion, height; public float amount; }
        public sealed class ReliefPixels { public int n; public Color32[] normal, occlusion, height; public float amount; }

        public const float ReliefNoise = .012f, ReliefRange = .05f, ReliefScale = .09f;

        public static Maps ReliefFor(Texture2D tile, float strength = 4f)
        {
            if (tile == null || tile.width != tile.height || !tile.isReadable) return null;
            return ReliefTextures(ReliefData(tile.GetPixels32(), tile.width, strength));
        }

        public static ReliefPixels ReliefData(Color32[] pixels, int n, float strength = 4f)
        {
            if (pixels == null || n < 8 || pixels.Length != n * n) return null;
            var lum = new float[n * n];
            var ca = new float[n * n];
            var cb = new float[n * n];
            for (int i = 0; i < lum.Length; i++)
            {
                float r = pixels[i].r / 255f, g = pixels[i].g / 255f, b = pixels[i].b / 255f;
                lum[i] = .299f * r + .587f * g + .114f * b;
                ca[i] = r - g;
                cb[i] = .5f * (r + g) - b;
            }
            var low = BlurWrap(lum, n, Mathf.Max(2, n / 16));
            var h = new float[lum.Length];
            for (int i = 0; i < h.Length; i++) h[i] = lum[i] - low[i];
            var fine = BlurWrap(h, n, 1);           // camera noise and JPEG blocks blur away
            var caS = BlurWrap(ca, n, 1);
            var cbS = BlurWrap(cb, n, 1);
            // A sharp change of hue is print or pattern (a logo, a stripe), not a bump.
            var weight = new float[h.Length];
            double sum = 0, weights = 0;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x, r = y * n + (x + 1) % n, l = y * n + (x + n - 1) % n, u = ((y + 1) % n) * n + x, d = ((y + n - 1) % n) * n + x;
                    float ga = (caS[r] - caS[l]) * .5f, gb = (cbS[r] - cbS[l]) * .5f, gc = (caS[u] - caS[d]) * .5f, gd = (cbS[u] - cbS[d]) * .5f;
                    float edge = Mathf.Sqrt(ga * ga + gb * gb + gc * gc + gd * gd);
                    float t = Mathf.Clamp01((edge - .03f) / .07f);
                    float w = 1f - t * t * (3f - 2f * t);
                    weight[i] = w;
                    sum += fine[i] * fine[i] * w;
                    weights += w;
                }
            float rms = weights > 1 ? Mathf.Sqrt((float)(sum / weights)) : 0f;
            float amount = Mathf.Clamp01((rms - ReliefNoise) / ReliefRange);
            var hn = new float[h.Length];
            for (int i = 0; i < hn.Length; i++) hn[i] = Mathf.Clamp(fine[i] * weight[i] / ReliefScale, -1f, 1f);
            var cavity = BlurWrap(hn, n, 4);
            var np = new Color32[n * n];
            var op = new Color32[n * n];
            var hp = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x;
                    float dx = hn[y * n + (x + 1) % n] - hn[y * n + (x + n - 1) % n];
                    float dy = hn[((y + 1) % n) * n + x] - hn[((y + n - 1) % n) * n + x];
                    var v = new Vector3(-dx * strength, -dy * strength, 1f);
                    v /= Mathf.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
                    // Packed so both encodings read it: RGB (mobile, XYZ) and RG-or-AG with alpha 1 (desktop).
                    np[i] = new Color32(Byte(v.x * .5f + .5f), Byte(v.y * .5f + .5f), Byte(v.z * .5f + .5f), 255);
                    float ao = 1f - Mathf.Clamp01((cavity[i] - hn[i]) * 1.1f) * .65f * amount;
                    op[i] = new Color32(Byte(ao), Byte(ao), Byte(ao), 255);
                    byte hh = Byte(.5f + .5f * hn[i]);
                    hp[i] = new Color32(hh, hh, hh, 255);
                }
            return new ReliefPixels { n = n, normal = np, occlusion = op, height = hp, amount = amount };
        }

        public static Maps ReliefTextures(ReliefPixels p)
        {
            if (p == null) return null;
            Texture2D Make(string name, Color32[] px)
            {
                var t = new Texture2D(p.n, p.n, TextureFormat.RGBA32, true, true) { name = name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear };
                t.SetPixels32(px);
                t.Apply(true, true); // no CPU copy kept
                return t;
            }
            return new Maps { normal = Make("Tile Normal", p.normal), occlusion = Make("Tile Occlusion", p.occlusion), height = Make("Tile Height", p.height), amount = p.amount };
        }

        private static byte Byte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private static float[] BlurWrap(float[] values, int n, int radius)
        {
            var rows = new float[values.Length];
            var result = new float[values.Length];
            float span = radius * 2 + 1;
            for (int y = 0; y < n; y++)
            {
                float sum = 0f;
                for (int k = -radius; k <= radius; k++) sum += values[y * n + ((k % n) + n) % n];
                for (int x = 0; x < n; x++)
                {
                    rows[y * n + x] = sum / span;
                    sum += values[y * n + (x + radius + 1) % n] - values[y * n + ((x - radius) % n + n) % n];
                }
            }
            for (int x = 0; x < n; x++)
            {
                float sum = 0f;
                for (int k = -radius; k <= radius; k++) sum += rows[(((k % n) + n) % n) * n + x];
                for (int y = 0; y < n; y++)
                {
                    result[y * n + x] = sum / span;
                    sum += rows[((y + radius + 1) % n) * n + x] - rows[(((y - radius) % n + n) % n) * n + x];
                }
            }
            return result;
        }

        // How different the two sides of each edge are when the tile repeats (0 = no seam), for tests.
        public static float SeamError(Texture2D tile)
        {
            var p = tile.GetPixels();
            int n = tile.width;
            float sum = 0f;
            for (int i = 0; i < n; i++)
            {
                sum += Diff(p[i * n], p[i * n + n - 1]) + Diff(p[i], p[(n - 1) * n + i]);
            }
            return sum / (2f * n);
        }

        // The same measure between neighbouring pixels inside the tile (its own grain), for comparison.
        public static float InnerStep(Texture2D tile)
        {
            var p = tile.GetPixels();
            int n = tile.width;
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += Diff(p[i * n + n / 2], p[i * n + n / 2 + 1]) + Diff(p[(n / 2) * n + i], p[(n / 2 + 1) * n + i]);
            return sum / (2f * n);
        }

        private static float Diff(Color a, Color b) => (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b)) / 3f;
    }
}
