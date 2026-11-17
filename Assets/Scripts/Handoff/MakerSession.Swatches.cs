using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Oculus.Interaction.Input;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // How the palette's contents look and arrive (2026-11-11, after the Quest try: the blobs were too small,
    // textures were saved as photos, the captures looked plain):
    //  - a colour is a glossy paint blob (2.7 cm across); a texture is a little material ball wrapped in its
    //    seamless tile (MaterialTile), so it reads as a material, not a photo;
    //  - picking a colour: a drop of that colour swells where the finger points, a ripple runs out, and the
    //    drop arcs into its slot with a trail of droplets, landing with a splash;
    //  - holding the pinch for a texture: viewfinder corners close in on the patch the finger points at;
    //    then a flash, and the material peels off the surface like a sticker and rolls into a dab on its way
    //    to the palette (MakerSession.Capture.cs).
    public sealed partial class MakerSession
    {
        public const float BlobRadius = .0135f;
        private Material texturedTemplate;
        private readonly Dictionary<(Texture2D, Vector2), Material> tileMaterials = new Dictionary<(Texture2D, Vector2), Material>();
        private readonly Dictionary<Texture2D, MaterialTile.Maps> reliefs = new Dictionary<Texture2D, MaterialTile.Maps>();
        private readonly Dictionary<string, Material> colourMaterials = new Dictionary<string, Material>();
        private readonly Dictionary<string, Mesh> blobMeshes = new Dictionary<string, Mesh>();
        private MaterialPropertyBlock tint;
        private Mesh flashMesh, barMesh;
        private readonly List<Transform> finderBars = new List<Transform>();

        // ------------------------------------------------------------ swatch looks

        private Transform PaintBlob(Color colour, float radius)
        {
            var root = new GameObject("Paint").transform;
            root.SetParent(transform, false);
            string key = ColorUtility.ToHtmlStringRGB(colour) + radius.ToString("0.0000");
            if (!blobMeshes.TryGetValue(key, out var mesh))
            {
                // F52: vertex colours reach the shader unconverted, so the picked (sRGB) colour goes in as linear;
                // it used to show washed out and lighter than what was picked.
                mesh = ToyMeshes.Disc(radius, radius * .5f, Lin(colour), Lin(Color.Lerp(colour, Color.black, .22f)), "PaintBlob");
                owned.Add(mesh);
                blobMeshes[key] = mesh;
            }
            Part(root, "Blob", mesh, Vector3.zero, 0f);
            // A small shine, so the paint looks wet.
            if (!blobMeshes.TryGetValue("shine", out var shine))
            {
                shine = ToyMeshes.Disc(1f, .1f, Color.white, Color.white, "PaintShine");
                owned.Add(shine);
                blobMeshes["shine"] = shine;
            }
            var spot = Part("Shine", shine);
            spot.SetParent(root, false);
            spot.localPosition = new Vector3(-radius * .38f, radius * .36f, -radius * .52f);
            spot.localScale = new Vector3(radius * .26f, radius * .18f, radius * .1f);
            return root;
        }

        // F52: picked colours and textures are shown as they were seen (Windups/True Colour, Resources/MakerTrueColour):
        // the user found them "dark and not the original" under URP Lit (virtual lights, an occlusion map darkening the
        // grain, parallax). Only the normal-map relief is kept, softer. Without the template the old URP Lit look stays.
        private Material trueTemplate;
        private bool trueLoaded;
        private Material TrueTemplate
        {
            get
            {
                if (!trueLoaded) { trueTemplate = Resources.Load<Material>("MakerTrueColour"); trueLoaded = true; }
                return trueTemplate;
            }
        }

        // The tile as a real material: its colour, plus relief made from it (a normal map), so a ball of it looks
        // like stone, wood or cloth, not a printed photo.
        // F57: the relief is only as strong as the surface really is rough (MaterialTile.ReliefData `amount`: a
        // smooth mug, glass or plastic gets none and a little shine instead), it is worked out off the main thread
        // (the material shows flat for the first frames), and `triplanar` (paint on a made thing) lays the tile in
        // real size from three sides instead of along the model's own texture layout, so it has no seams or
        // stretched patches.
        // F70 (2026-11-14): paint on made things is clean and flat - no shine (PaintGloss 0) and no relief on a
        // painted texture (the user found the paint "bumpy, like brushed paint"). The palette's own dabs and
        // balls keep their relief, so a picked material still reads as stone, wood or cloth there.
        public const float TileRelief = .55f, TileGlossSmooth = .32f, PaintGloss = 0f;
        private readonly Dictionary<Texture2D, List<Material>> waitingRelief = new Dictionary<Texture2D, List<Material>>();

        private Material TileMaterial(Texture2D tile, Vector2 tiling, bool triplanar = false)
        {
            if (tile == null) return material;
            var key = (tile, triplanar ? new Vector2(-1f, -1f) : tiling);
            if (tileMaterials.TryGetValue(key, out var m) && m != null) return m;
            if (TrueTemplate != null)
            {
                m = new Material(TrueTemplate) { name = triplanar ? "Material Paint" : "Material Tile" };
                m.SetTexture("_BaseMap", tile);
                m.SetTextureScale("_BaseMap", tiling);
                m.SetColor("_BaseColor", Color.white);
                if (m.HasProperty("_Triplanar")) m.SetFloat("_Triplanar", triplanar ? 1f : 0f);
                if (m.HasProperty("_TriTile")) m.SetFloat("_TriTile", PaintableThing.TileSize);
                m.SetFloat("_BumpScale", 0f);
                owned.Add(m);
                tileMaterials[key] = m;
                if (triplanar) { if (m.HasProperty("_Gloss")) m.SetFloat("_Gloss", PaintGloss); return m; } // F70: painted = flat
                if (reliefs.TryGetValue(tile, out var relief) && relief != null) ApplyRelief(m, relief);
                else RequestRelief(tile, m);
                return m;
            }
            if (texturedTemplate == null) texturedTemplate = Resources.Load<Material>("MakerTextured");
            m = texturedTemplate != null ? new Material(texturedTemplate) : new Material(material);
            m.name = "Material Tile";
            if (m.HasProperty("_BaseMap")) { m.SetTexture("_BaseMap", tile); m.SetTextureScale("_BaseMap", tiling); }
            m.mainTexture = tile;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", .28f);
            if (!reliefs.TryGetValue(tile, out var maps) || maps == null)
            {
                maps = MaterialTile.ReliefFor(tile);
                reliefs[tile] = maps;
                if (maps != null) { owned.Add(maps.normal); owned.Add(maps.occlusion); owned.Add(maps.height); }
            }
            if (maps != null && maps.amount > .05f)
            {
                m.SetTexture("_BumpMap", maps.normal);
                m.SetFloat("_BumpScale", maps.amount);
                m.EnableKeyword("_NORMALMAP");
                m.SetTexture("_OcclusionMap", maps.occlusion);
                m.SetFloat("_OcclusionStrength", 1f);
                m.EnableKeyword("_OCCLUSIONMAP");
                m.SetTexture("_ParallaxMap", maps.height);
                m.SetFloat("_Parallax", .018f * maps.amount);
                m.EnableKeyword("_PARALLAXMAP");
            }
            // Its own colour glows softly, so the side away from the light still shows the material.
            m.SetTexture("_EmissionMap", tile);
            m.SetTextureScale("_EmissionMap", tiling);
            m.SetColor("_EmissionColor", new Color(.34f, .34f, .34f));
            m.EnableKeyword("_EMISSION");
            owned.Add(m);
            tileMaterials[key] = m;
            return m;
        }

        // F57: relief for a tile, made off the main thread (one read of the tile's pixels here, the blurs in a task);
        // every material of that tile gets it when it is ready.
        private void RequestRelief(Texture2D tile, Material m)
        {
            if (waitingRelief.TryGetValue(tile, out var list)) { list.Add(m); return; }
            if (!tile.isReadable || tile.width != tile.height) return;
            waitingRelief[tile] = new List<Material> { m };
            var pixels = tile.GetPixels32();
            int n = tile.width;
            StartCoroutine(ReliefLater(tile, System.Threading.Tasks.Task.Run(() => MaterialTile.ReliefData(pixels, n))));
        }

        private IEnumerator ReliefLater(Texture2D tile, System.Threading.Tasks.Task<MaterialTile.ReliefPixels> work)
        {
            while (!work.IsCompleted) yield return null;
            waitingRelief.TryGetValue(tile, out var waiting);
            waitingRelief.Remove(tile);
            if (work.IsFaulted || work.Result == null || tile == null) yield break;
            var relief = MaterialTile.ReliefTextures(work.Result);
            reliefs[tile] = relief;
            owned.Add(relief.normal); owned.Add(relief.occlusion); owned.Add(relief.height);
            if (waiting != null) foreach (var m in waiting) if (m != null) ApplyRelief(m, relief);
        }

        // Only as much relief as the surface has; a smooth one gets a soft shine instead (paint, glaze, plastic).
        private static void ApplyRelief(Material m, MaterialTile.Maps relief)
        {
            float amount = relief != null ? relief.amount : 0f;
            if (relief != null && amount > .05f) m.SetTexture("_BumpMap", relief.normal);
            m.SetFloat("_BumpScale", amount > .05f ? TileRelief * amount : 0f);
            if (m.HasProperty("_Gloss")) m.SetFloat("_Gloss", TileGlossSmooth * (1f - amount));
        }

        public float ReliefAmount(Texture2D tile) => tile != null && reliefs.TryGetValue(tile, out var r) && r != null ? r.amount : -1f;

        // A plain colour as the same kind of material (for painting a part).
        private Material ColourMaterial(Color colour)
        {
            string key = ColorUtility.ToHtmlStringRGB(colour);
            if (colourMaterials.TryGetValue(key, out var m) && m != null) return m;
            if (TrueTemplate != null)
            {
                m = new Material(TrueTemplate) { name = "Paint " + key };
                m.SetTexture("_BaseMap", Texture2D.whiteTexture);
                m.SetColor("_BaseColor", colour);
                m.SetFloat("_BumpScale", 0f);
                if (m.HasProperty("_Shade")) m.SetFloat("_Shade", PaintShade); // F69
                if (m.HasProperty("_Gloss")) m.SetFloat("_Gloss", PaintGloss); // F70: clean flat colour, no shine
                owned.Add(m);
                colourMaterials[key] = m;
                return m;
            }
            if (texturedTemplate == null) texturedTemplate = Resources.Load<Material>("MakerTextured");
            m = texturedTemplate != null ? new Material(texturedTemplate) : new Material(material);
            m.name = "Paint " + key;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", Texture2D.whiteTexture);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", .45f);
            m.SetTexture("_EmissionMap", Texture2D.whiteTexture);
            m.SetColor("_EmissionColor", colour * .34f);
            m.EnableKeyword("_EMISSION");
            m.DisableKeyword("_PARALLAXMAP");
            m.SetFloat("_Parallax", 0f);
            owned.Add(m);
            colourMaterials[key] = m;
            return m;
        }

        private Transform MaterialBall(Texture2D tile, float radius)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(go.GetComponent<Collider>());
            go.name = "Material Ball";
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * radius * 2f;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = TileMaterial(tile, new Vector2(2f, 1f));
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        // A puffy round dab (front toward -Z, like the colour blobs) wearing the material.
        private Mesh dabMesh;
        private Transform MaterialDab(Texture2D tile, float radius)
        {
            if (dabMesh == null) { dabMesh = DabMesh(); owned.Add(dabMesh); }
            var go = new GameObject("Material Dab", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * radius;
            go.GetComponent<MeshFilter>().sharedMesh = dabMesh;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = TileMaterial(tile, Vector2.one);
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        // Unit radius, half a unit thick: a flat face (UV across it), a rounded bevel, the side and the back.
        private static Mesh DabMesh()
        {
            const int n = 40;
            var v = new List<Vector3>();
            var nr = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();
            void Tri(int a, int b, int c, Vector3 facing)
            {
                if (Vector3.Dot(Vector3.Cross(v[b] - v[a], v[c] - v[a]), facing) < 0f) { int t = b; b = c; c = t; }
                tri.AddRange(new[] { a, b, c });
            }
            // Rings: front face edge (r .8, z -.25), bevel (r .97, z -.12), side (r 1, z .1), back edge (r .9, z .25).
            float[] radius = { .8f, .97f, 1f, .9f };
            float[] depth = { -.25f, -.12f, .1f, .25f };
            int front = v.Count;
            v.Add(new Vector3(0f, 0f, -.27f)); nr.Add(Vector3.back); uv.Add(new Vector2(.5f, .5f));
            int[] ring = new int[radius.Length];
            for (int k = 0; k < radius.Length; k++)
            {
                ring[k] = v.Count;
                for (int i = 0; i <= n; i++)
                {
                    float a = i / (float)n * Mathf.PI * 2f;
                    var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    v.Add(dir * radius[k] + Vector3.forward * depth[k]);
                    Vector3 normal = k == 0 ? (Vector3.back * .8f + dir * .2f).normalized : k == 1 ? (Vector3.back * .5f + dir).normalized : k == 2 ? dir : (Vector3.forward * .7f + dir * .5f).normalized;
                    nr.Add(normal);
                    uv.Add(new Vector2(dir.x * radius[k] * .5f + .5f, dir.y * radius[k] * .5f + .5f));
                }
            }
            int back = v.Count;
            v.Add(new Vector3(0f, 0f, .25f)); nr.Add(Vector3.forward); uv.Add(new Vector2(.5f, .5f));
            for (int i = 0; i < n; i++)
            {
                Tri(front, ring[0] + i, ring[0] + i + 1, Vector3.back);
                for (int k = 0; k + 1 < radius.Length; k++)
                {
                    int a = ring[k] + i, b = ring[k] + i + 1, c = ring[k + 1] + i, d = ring[k + 1] + i + 1;
                    Vector3 outward = (nr[a] + nr[c]).normalized;
                    Tri(a, c, b, outward);
                    Tri(b, c, d, outward);
                }
                Tri(back, ring[radius.Length - 1] + i, ring[radius.Length - 1] + i + 1, Vector3.forward);
            }
            var mesh = new Mesh { name = "Material Dab" };
            mesh.SetVertices(v);
            mesh.SetNormals(nr);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        // F69: a ball of colour (the pick's drop, its beads and ring, a swatch flying to the palette) shows the colour
        // itself. Since F65 the session's `material` is the crew's shader (a warm light, a lavender shade, a lift toward
        // the top), which tinted the drop: the colour at the fingertips was not the colour that was taken.
        public const float PaintShade = .92f; // the far side of paint: only a little darker (True Colour _Shade)
        private Material paintBall;
        private Material PaintBallMaterial
        {
            get
            {
                if (paintBall != null || TrueTemplate == null) return paintBall;
                paintBall = new Material(TrueTemplate) { name = "Paint Ball" };
                paintBall.SetTexture("_BaseMap", Texture2D.whiteTexture);
                paintBall.SetColor("_BaseColor", Color.white);
                paintBall.SetFloat("_BumpScale", 0f);
                if (paintBall.HasProperty("_Shade")) paintBall.SetFloat("_Shade", PaintShade);
                if (paintBall.HasProperty("_Gloss")) paintBall.SetFloat("_Gloss", PaintGloss);
                owned.Add(paintBall);
                return paintBall;
            }
        }

        private Transform ColourBall(string name, Color colour, float radius)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * radius * 2f;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = PaintBallMaterial ?? material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            tint ??= new MaterialPropertyBlock();
            tint.Clear();
            tint.SetColor("_BaseColor", colour);
            tint.SetFloat("_VertexColor", 0f);
            r.SetPropertyBlock(tint);
            return go.transform;
        }

        // A paint blob for a colour, a material ball for a texture (sitting on the board, facing the person).
        // 2026-11-15: on the designed palette a swatch is that slot's own paint blob (MakerSession.PaletteModel.cs).
        private Transform SwatchView(Swatch swatch, float radius, int slot = -1)
        {
            var root = new GameObject("Swatch " + swatch.name).transform;
            root.SetParent(transform, false);
            Color c = Hex(swatch.hex);
            Texture2D tile = swatch.kind == "texture" && swatch.image != null && System.IO.File.Exists(swatch.image) ? LoadSwatchTexture(swatch.image) : null;
            if (paintMeshes.Count > 0)
            {
                var blob = PaletteBlob(c, tile, slot);
                blob.SetParent(root, false);
                blob.localPosition = Vector3.zero;
                blob.localRotation = Quaternion.identity;
            }
            else if (tile != null)
            {
                // The same paint-dab shape as a colour, made of the material.
                var dab = MaterialDab(tile, radius * .9f);
                dab.SetParent(root, false);
                dab.localPosition = Vector3.zero;
                dab.localRotation = Quaternion.identity;
            }
            else
            {
                var blob = PaintBlob(c, radius);
                blob.SetParent(root, false);
            }
            return root;
        }

        private Transform ViewOf(Swatch swatch)
        {
            var shown = Palette.Skip(Mathf.Max(0, Palette.Count - PaletteSlots)).ToList();
            int i = shown.IndexOf(swatch);
            return i >= 0 && i < swatchViews.Count ? swatchViews[i] : null;
        }

        // ------------------------------------------------------------ a colour arrives

        // The colour pools where the finger points and pulls up into a drop, which flies to its slot.
        // F52: a pinch's drop (filled at the fingers) flies straight in.
        private IEnumerator ColourArrives(Swatch swatch, Color colour, Vector3 at, Transform pinched = null)
        {
            if (pinched != null)
            {
                TintBall(pinched, colour);
                yield return FlyInto(pinched, swatch, colour, null);
                yield break;
            }
            Transform drop = null;
            yield return Puddle(colour, at, SurfaceNormal(at), d => drop = d);
            if (drop == null) yield break;
            yield return new WaitForSeconds(.06f);
            yield return FlyInto(drop, swatch, colour, null);
        }

        // ------------------------------------------------------------ a texture arrives

        private IEnumerator TextureArrives(Swatch swatch, Texture2D tile, Vector3 centre, Quaternion facing, float width, float height, bool peel = false)
        {
            // The flash fills the frame and snaps away.
            if (flashMesh == null) { flashMesh = ToyMeshes.RoundedSlab(1f, 1f, .06f, .001f, Color.white, Color.white, Color.white, "Flash"); owned.Add(flashMesh); }
            var flash = Part("Flash", flashMesh);
            flash.SetPositionAndRotation(centre, facing);
            for (float t = 0f; t < 1f && flash != null; t += Time.deltaTime / .18f)
            {
                float s = 1f - t * t;
                flash.localScale = new Vector3(width * s, height * s, 1f);
                yield return null;
            }
            if (flash != null) Destroy(flash.gameObject);
            if (peel && tile != null)
            {
                // Lying on a surface: it peels off like a sticker and rolls up into a dab.
                Transform rolled = null;
                yield return PeelOff(tile, centre, facing, width, height, d => rolled = d);
                if (rolled != null) yield return FlyInto(rolled, swatch, null, null, BlobRadius);
                yield break;
            }
            // The material shows in the frame as a big ball, turning so its relief catches the light ...
            float size = Mathf.Clamp(Mathf.Min(width, height) * .7f, .05f, .09f);
            // A big dab of the material faces the person, rocking a little so its relief catches the light.
            var ball = MaterialDab(tile, size * .5f);
            ball.SetPositionAndRotation(centre, facing);
            for (float t = 0f; t < 1f && ball != null; t += Time.deltaTime / .25f)
            {
                ball.localScale = Vector3.one * size * .5f * Overshoot(t);
                yield return null;
            }
            ToySounds.Play(ToySounds.Cue.Chime, centre, .4f, 1.6f);
            for (float t = 0f; t < 1f && ball != null; t += Time.deltaTime)
            {
                ball.rotation = facing * Quaternion.Euler(Mathf.Sin(t * 6f) * 14f, Mathf.Sin(t * 4.3f) * 18f, 0f);
                yield return null;
            }
            // ... then flies into the palette, shrinking to a swatch.
            if (ball != null) yield return FlyInto(ball, swatch, null, null, BlobRadius);
        }

        // The flyer arcs into the swatch's slot (hidden until it lands), a card shrinking away beside it.
        private IEnumerator FlyInto(Transform flyer, Swatch swatch, Color? trail, Transform card, float endSize = -1f)
        {
            var slot = ViewOf(swatch);
            if (slot != null) slot.localScale = Vector3.zero;
            // F84: the palette is not on the hand (a texture framed without it): it flies to Spark's little palette.
            bool toSpark = SparkTakesSwatch();
            Vector3 from = flyer.position;
            Vector3 startScale = flyer.localScale;
            Vector3 cardScale = card != null ? card.localScale : Vector3.zero;
            float nextTrail = 0f;
            for (float t = 0f; t < 1f && flyer != null; t += Time.deltaTime / .6f)
            {
                Vector3 to = toSpark ? SparkSwatchPoint(from) : slot != null ? slot.position : paletteBoard != null ? paletteBoard.position : from;
                float e = Mathf.SmoothStep(0f, 1f, t);
                flyer.position = Vector3.Lerp(from, to, e) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .09f;
                if (card != null)
                {
                    card.position = Vector3.Lerp(card.position, flyer.position, .35f);
                    card.rotation = SpeechBubbleDock.FacingAt(card.position) * Quaternion.Euler(0f, 180f, 0f);
                    card.localScale = cardScale * (1f - Mathf.Clamp01(t / .55f));
                    flyer.localScale = Vector3.one * BlobRadius * 1.84f * Mathf.Clamp01((t - .2f) / .5f);
                }
                else if (endSize > 0f) { flyer.localScale = Vector3.Lerp(startScale, Vector3.one * endSize, e); flyer.rotation = SpeechBubbleDock.FacingAt(flyer.position); }
                else flyer.localScale = startScale * Mathf.Lerp(1f, .85f, e);
                if (trail.HasValue && Time.time > nextTrail)
                {
                    nextTrail = Time.time + .035f;
                    StartCoroutine(TrailDrop(flyer.position, trail.Value));
                }
                yield return null;
            }
            if (card != null) Destroy(card.gameObject);
            if (flyer != null) Destroy(flyer.gameObject);
            if (toSpark)
            {
                // Into Spark's palette: a pop there, the swatch waiting in its slot for when the palette comes out.
                if (slot != null) slot.localScale = Vector3.one;
                Vector3 at = SparkSwatchPoint(from);
                ToySounds.Play(ToySounds.Cue.Pop, at, .45f, 1.1f);
                StartCoroutine(Ripple(at, trail ?? new Color(1f, .95f, .85f), .35f));
                if (sparkPalette != null) StartCoroutine(Bump(sparkPalette));
                TexturesToSpark++;
                yield break;
            }
            Vector3 landed = slot != null ? slot.position : paletteBoard != null ? paletteBoard.position : from;
            if (slot != null && trail.HasValue) { slot.localScale = Vector3.one; StartCoroutine(Splat(slot, trail.Value)); }
            else
            {
                if (slot != null) StartCoroutine(Grow(slot, 1f, .22f));
                ToySounds.Play(ToySounds.Cue.Pop, landed, .45f, 1.1f);
            }
            StartCoroutine(Ripple(landed, trail ?? new Color(1f, .95f, .85f), .5f));
            if (paletteBoard != null) StartCoroutine(Bump(paletteBoard));
        }

        private IEnumerator TrailDrop(Vector3 at, Color colour)
        {
            var bit = ColourBall("Trail", colour, .004f);
            bit.position = at;
            Vector3 fall = Random.insideUnitSphere * .02f + Vector3.down * .03f;
            for (float t = 0f; t < 1f && bit != null; t += Time.deltaTime / .32f)
            {
                bit.position += fall * Time.deltaTime;
                bit.localScale = Vector3.one * .008f * (1f - t);
                yield return null;
            }
            if (bit != null) Destroy(bit.gameObject);
        }

        // A thin ring of the colour running out, facing the person.
        private IEnumerator Ripple(Vector3 at, Color colour, float size = 1f)
        {
            var ring = new GameObject("Ripple", typeof(MeshFilter), typeof(MeshRenderer)).transform;
            ring.SetParent(transform, false);
            ring.GetComponent<MeshFilter>().sharedMesh = SeenRingMesh();
            var r = ring.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            tint ??= new MaterialPropertyBlock();
            tint.Clear();
            tint.SetColor("_BaseColor", colour);
            tint.SetFloat("_VertexColor", 0f);
            r.SetPropertyBlock(tint);
            Vector3 toEye = Head != null ? (Head.position - at).normalized : Vector3.back;
            ring.SetPositionAndRotation(at, Quaternion.LookRotation(toEye) * Quaternion.Euler(90f, 0f, 0f));
            for (float t = 0f; t < 1f && ring != null; t += Time.deltaTime / .4f)
            {
                float grow = 1f - (1f - t) * (1f - t);
                ring.localScale = new Vector3(1f, 1f - t * .7f, 1f) * Mathf.Lerp(.012f, .07f, grow) * size;
                yield return null;
            }
            if (ring != null) Destroy(ring.gameObject);
        }

        private static float Overshoot(float t)
        {
            t = Mathf.Clamp01(t);
            return 1f + 2.4f * Mathf.Pow(t - 1f, 3f) + 1.4f * Mathf.Pow(t - 1f, 2f);
        }

        // ------------------------------------------------------------ the texture frame's viewfinder

        // Corner brackets close in on the frame while the hands hold still (progress 0 to 1), with thin edges
        // drawing around it.
        private void ShowFramePreview(Vector3 centre, Quaternion facing, float width, float height, float progress)
        {
            if (Head == null) return;
            if (framePreview == null)
            {
                framePreview = new GameObject("Frame Preview").transform;
                framePreview.SetParent(transform, false);
                barMesh = ToyMeshes.RoundedSlab(1f, 1f, .0005f, .001f, Color.white, Color.white, Color.white, "FinderBar");
                owned.Add(barMesh);
                for (int i = 0; i < 12; i++)
                {
                    var bar = Part("Bar", barMesh);
                    bar.SetParent(framePreview, false);
                    finderBars.Add(bar);
                }
            }
            framePreview.gameObject.SetActive(true);
            framePreview.SetPositionAndRotation(centre, facing);
            float p = Mathf.SmoothStep(0f, 1f, progress);
            float inset = (1f - p) * .014f, hw = width * .5f + inset, hh = height * .5f + inset;
            float arm = Mathf.Min(width, height) * .28f, thick = .0035f + .0025f * p;
            int k = 0;
            foreach (var (sx, sy) in new[] { (-1f, 1f), (1f, 1f), (-1f, -1f), (1f, -1f) })
            {
                finderBars[k].localPosition = new Vector3(sx * (hw - arm * .5f), sy * hh, 0f);
                finderBars[k++].localScale = new Vector3(arm, thick, 1f);
                finderBars[k].localPosition = new Vector3(sx * hw, sy * (hh - arm * .5f), 0f);
                finderBars[k++].localScale = new Vector3(thick, arm, 1f);
            }
            // The edges draw in from the corners as the hold fills.
            float edgeW = Mathf.Max(.0001f, (width - 2f * arm) * p), edgeH = Mathf.Max(.0001f, (height - 2f * arm) * p);
            finderBars[k].localPosition = new Vector3(0f, hh, 0f); finderBars[k++].localScale = new Vector3(edgeW, .0012f, 1f);
            finderBars[k].localPosition = new Vector3(0f, -hh, 0f); finderBars[k++].localScale = new Vector3(edgeW, .0012f, 1f);
            finderBars[k].localPosition = new Vector3(-hw, 0f, 0f); finderBars[k++].localScale = new Vector3(.0012f, edgeH, 1f);
            finderBars[k].localPosition = new Vector3(hw, 0f, 0f); finderBars[k].localScale = new Vector3(.0012f, edgeH, 1f);
        }

        private void HideFramePreview() { if (framePreview != null) framePreview.gameObject.SetActive(false); }
    }
}
