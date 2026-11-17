using UnityEngine;

namespace Windups
{
    // Emoji-style reaction stickers drawn in code (no font or image assets): thumbs up,
    // heart eyes, thinking face and thumbs down, each with a white die-cut border and a soft
    // shadow so it reads as a physical sticker. Sprites are generated once and cached.
    public static class EmojiStickers
    {
        public const int Like = 0, Love = 1, Hmm = 2, Nah = 3;
        public static readonly string[] Names = { "Like", "Love", "Hmm", "Nah" };
        private const int Size = 192;
        private static readonly Sprite[] Cache = new Sprite[4];

        private static readonly Color Light = new Color(1f, .89f, .42f);
        private static readonly Color Deep = new Color(.98f, .70f, .10f);
        private static readonly Color Rim = new Color(.86f, .56f, .05f);
        private static readonly Color Ink = new Color(.33f, .19f, .06f);

        public static Sprite Get(int index)
        {
            index = Mathf.Clamp(index, 0, 3);
            if (Cache[index] != null) return Cache[index];
            var texture = Draw(index);
            Cache[index] = Sprite.Create(texture, new Rect(0, 0, Size, Size), new Vector2(.5f, .5f), Size);
            Cache[index].name = "Emoji " + Names[index];
            return Cache[index];
        }

        public static Texture2D Draw(int index, bool keepReadable = false)
        {
            var pixels = new Color[Size * Size];
            float px = 2f / Size; // one pixel in shape units ([-1, 1] across)
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var p = new Vector2((x + .5f) * px - 1f, (y + .5f) * px - 1f);
                pixels[y * Size + x] = Shade(index, p, px);
            }
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, true) { name = "Emoji " + Names[index] };
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = 2;
            texture.SetPixels(pixels);
            texture.Apply(true, !keepReadable);
            return texture;
        }

        // ------------------------------------------------------------------ shapes

        private static float Circle(Vector2 p, Vector2 c, float r) => (p - c).magnitude - r;

        private static float Ellipse(Vector2 p, Vector2 c, Vector2 r)
        {
            Vector2 q = new Vector2((p.x - c.x) / r.x, (p.y - c.y) / r.y);
            return (q.magnitude - 1f) * Mathf.Min(r.x, r.y);
        }

        private static float Box(Vector2 p, Vector2 c, Vector2 half, float radius)
        {
            Vector2 d = new Vector2(Mathf.Abs(p.x - c.x), Mathf.Abs(p.y - c.y)) - half + Vector2.one * radius;
            return new Vector2(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f)).magnitude + Mathf.Min(Mathf.Max(d.x, d.y), 0f) - radius;
        }

        private static float Segment(Vector2 p, Vector2 a, Vector2 b, float r)
        {
            Vector2 pa = p - a, ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
            return (pa - ba * h).magnitude - r;
        }

        // Heart centred at c, about 2*s wide, pointing down.
        private static float Heart(Vector2 p, Vector2 c, float s)
        {
            Vector2 q = (p - c) / s;
            q.y += .55f;
            q.x = Mathf.Abs(q.x);
            float d;
            if (q.y + q.x > 1f) d = (q - new Vector2(.25f, .75f)).magnitude - .3535534f;
            else
            {
                float a = (q - new Vector2(0f, 1f)).sqrMagnitude;
                float t = .5f * Mathf.Max(q.x + q.y, 0f);
                float b = (q - new Vector2(t, t)).sqrMagnitude;
                d = Mathf.Sqrt(Mathf.Min(a, b)) * Mathf.Sign(q.x - q.y);
            }
            return d * s;
        }

        private static float Cover(float d, float px) => Mathf.Clamp01(.5f - d / px);

        private static Color Over(Color under, Color over, float alpha)
        {
            float a = over.a * alpha;
            float outA = a + under.a * (1f - a);
            if (outA < 1e-4f) return Color.clear;
            Color c = (over * a + under * under.a * (1f - a)) / outA;
            c.a = outA;
            return c;
        }

        private static Color Gradient(Vector2 p, Vector2 light, float reach)
        {
            float t = Mathf.Clamp01((p - light).magnitude / reach);
            return Color.Lerp(Light, Deep, t * t * (3f - 2f * t));
        }

        // ------------------------------------------------------------------ emoji

        private static Vector2 Rotate(Vector2 p, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(c * p.x - s * p.y, s * p.x + c * p.y);
        }

        // Thumb up: a rounded fist with the thumb raised.
        private static float Fist(Vector2 p) => Box(p, new Vector2(.06f, -.24f), new Vector2(.44f, .36f), .2f);
        private static float Thumb(Vector2 p) => Segment(p, new Vector2(-.2f, -.02f), new Vector2(-.1f, .62f), .17f);
        private static float Hand(Vector2 p) => Mathf.Min(Fist(p), Thumb(p));

        private static float Silhouette(int index, Vector2 p)
        {
            switch (index)
            {
                case Like: return Hand(p);
                case Nah: return Hand(-p);
                case Hmm: return Mathf.Min(Circle(p, Vector2.zero, .76f), ThinkingHand(p));
                default: return Circle(p, Vector2.zero, .8f);
            }
        }

        private static float ThinkingHand(Vector2 p)
        {
            Vector2 q = Rotate(p - new Vector2(.24f, -.68f), -20f);
            float palm = Box(q, Vector2.zero, new Vector2(.27f, .15f), .14f);
            float finger = Segment(p, new Vector2(.06f, -.6f), new Vector2(-.12f, -.38f), .095f);
            return Mathf.Min(palm, finger);
        }

        private static Color Shade(int index, Vector2 p, float px)
        {
            Color c = Color.clear;
            float shape = Silhouette(index, p);
            // Soft shadow, then the white die-cut border, then a hairline grey edge.
            float shadow = Silhouette(index, p - new Vector2(.03f, -.05f)) - .12f;
            c = Over(c, new Color(0f, 0f, 0f, .22f), Mathf.Clamp01(.5f - shadow / .09f));
            c = Over(c, new Color(.82f, .82f, .86f), Cover(shape - .13f, px));
            c = Over(c, Color.white, Cover(shape - .115f, px));
            switch (index)
            {
                case Like: c = DrawHand(c, p, px); break;
                case Nah: c = DrawHand(c, -p, px); break;
                case Love: c = DrawLove(c, p, px); break;
                case Hmm: c = DrawHmm(c, p, px); break;
            }
            return c;
        }

        private static Color DrawFace(Color c, Vector2 p, float px, float radius)
        {
            float face = Circle(p, Vector2.zero, radius);
            c = Over(c, Rim, Cover(face, px));
            c = Over(c, Gradient(p, new Vector2(-.3f, .35f), 1.25f), Cover(face + .035f, px));
            // A soft highlight on the upper left.
            c = Over(c, new Color(1f, 1f, 1f, .35f), Cover(Ellipse(p, new Vector2(-.32f, .42f), new Vector2(.2f, .11f)), px * 6f));
            return c;
        }

        private static Color DrawHand(Color c, Vector2 p, float px)
        {
            float hand = Hand(p);
            c = Over(c, Rim, Cover(hand, px));
            c = Over(c, Gradient(p, new Vector2(-.25f, .45f), 1.3f), Cover(hand + .035f, px));
            // Finger creases on the fist and the thumb's knuckle line.
            for (int i = 0; i < 3; i++)
            {
                float y = .0f - i * .2f;
                c = Over(c, Rim, Cover(Segment(p, new Vector2(.12f, y), new Vector2(.42f, y + .01f), .022f), px));
            }
            c = Over(c, Rim, Cover(Segment(p, new Vector2(-.33f, -.05f), new Vector2(-.05f, -.02f), .02f), px));
            c = Over(c, new Color(1f, 1f, 1f, .35f), Cover(Ellipse(p, new Vector2(-.17f, .45f), new Vector2(.05f, .12f)), px * 5f));
            return c;
        }

        private static Color DrawLove(Color c, Vector2 p, float px)
        {
            c = DrawFace(c, p, px, .8f);
            // Big grin: dark mouth with a tongue.
            float mouth = Mathf.Max(Circle(p, new Vector2(0f, -.12f), .46f), p.y + .16f);
            c = Over(c, Ink, Cover(mouth, px));
            float tongue = Mathf.Max(Circle(p, new Vector2(0f, -.6f), .2f), mouth);
            c = Over(c, new Color(.95f, .38f, .45f), Cover(tongue, px));
            float teeth = Mathf.Max(mouth, -(p.y + .26f));
            c = Over(c, Color.white, Cover(teeth, px));
            // Heart eyes.
            for (int side = -1; side <= 1; side += 2)
            {
                var at = new Vector2(.31f * side, .2f);
                float heart = Heart(p, at, .27f);
                c = Over(c, new Color(.80f, .10f, .20f), Cover(heart, px));
                c = Over(c, new Color(.98f, .22f, .32f), Cover(heart + .025f, px));
                c = Over(c, new Color(1f, 1f, 1f, .6f), Cover(Circle(p, at + new Vector2(-.09f, .08f), .05f), px));
            }
            return c;
        }

        private static Color DrawHmm(Color c, Vector2 p, float px)
        {
            c = DrawFace(c, p, px, .76f);
            // Eyes, one raised eyebrow, a doubtful straight mouth.
            c = Over(c, Ink, Cover(Ellipse(p, new Vector2(-.26f, .12f), new Vector2(.075f, .12f)), px));
            c = Over(c, Ink, Cover(Ellipse(p, new Vector2(.26f, .1f), new Vector2(.075f, .12f)), px));
            c = Over(c, Ink, Cover(Segment(p, new Vector2(-.42f, .36f), new Vector2(-.12f, .45f), .04f), px));
            c = Over(c, Ink, Cover(Segment(p, new Vector2(.12f, .32f), new Vector2(.4f, .3f), .04f), px));
            c = Over(c, Ink, Cover(Segment(p, new Vector2(-.3f, -.26f), new Vector2(.02f, -.2f), .045f), px));
            // The hand at the chin.
            float hand = ThinkingHand(p);
            c = Over(c, Rim, Cover(hand, px));
            c = Over(c, Gradient(p, new Vector2(.0f, -.4f), .8f), Cover(hand + .03f, px));
            Vector2 q = Rotate(p - new Vector2(.24f, -.68f), -20f);
            c = Over(c, Rim, Cover(Segment(q, new Vector2(.0f, -.03f), new Vector2(.2f, -.03f), .018f), px));
            return c;
        }
    }
}
