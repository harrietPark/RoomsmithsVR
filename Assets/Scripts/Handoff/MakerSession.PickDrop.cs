using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // F52 (2026-11-12, after a Quest try: "I pinched the desk but the circle ended up way down there; could it look
    // more like paint? I can't even tell whether I got the colour"): a colour pick now happens at the fingers.
    //  - The pinch makes a small clear bead between the fingertips (no ring out on a surface: the room's model can
    //    put that far from what the person sees, as on that try, where it landed on the floor).
    //  - As soon as the camera picture taken at the pinch is in (a few frames), the colour is read there and the bead
    //    fills with it and swells, paint is drawn up into it from the spot (a wet spot first when the fingers are at
    //    the surface), and a ring of that colour stays round the fingers while they hold: the person sees what they
    //    took before letting go.
    //  - Let go: that drop flies into the palette (the F29 flight and splat). Cancelled: it pops.
    //  - Each pick also leaves a small crop of the picture with the spot marked in eyes/pick_*.jpg (packed and written
    //    off the main thread), so a wrong colour can be checked later.
    // F90 (2026-11-15, the user: "the colour pick only has a circle effect - something else?"; picked draft A "Pipette"
    // of Docs/Design/Concepts/Windups_ColourPickVfx_2026-11-15): the bead and its ring round the fingers became a small
    // glass pipette.
    //  1. The pinch: a pipette pops in, its red rubber bulb between the fingers, the glass hanging below (tipped toward a
    //     spot close below the hand, else a little away from the person); it sways after the hand.
    //  2. The colour is read: the bulb squeezes and lets go, the paint twists up into the tip in a spiral (from a wet
    //     spot on the surface when the fingers are close to it) and the glass fills from the bottom.
    //  3. Full: the glass takes the colour's tint, a sparkle and a ding.
    //  4. Let go: the bulb squeezes, a drop comes out of the tip and arcs into the palette (the F29 flight and splat);
    //     the pipette shrinks away. Cancelled: it pops.
    // No rings any more (F52's halo and ripple round the fingers were the "circle").
    public sealed partial class MakerSession
    {
        public const float DropRadius = .009f;
        public const float PipetteTube = .045f, PipetteRadius = .0045f, PipetteNozzle = .011f, BulbRadius = .0068f, SipSeconds = .55f;
        public bool DropShown => dropper != null;
        public Color? DropColour { get; private set; }
        public int DropsFilled { get; private set; }
        public float PipetteFill => pipe != null ? pipe.fill : 0f;   // checks: 0 empty .. 1 full
        public bool PipetteFull { get; private set; }
        public string LastPickCrop { get; private set; } = "";
        private Transform dropper;          // the pipette while the pinch is held
        private Pipette pipe;
        private float dropFilledAt = -1f, dropBornAt;
        private Vector3 pipetteAxis = Vector3.down;

        private sealed class Pipette
        {
            public Transform root, bulb, liquid, nozzleLiquid;
            public Material glass;
            public float fill, squeezeAt = -10f;
            public Color colour;
            public bool full;
        }

        private static readonly Color Rubber = new Color(.93f, .34f, .32f), RubberDark = new Color(.72f, .22f, .22f), GlassTint = new Color(.86f, .95f, 1f, 1f);

        private void StartDropper(Vector3 at)
        {
            EndDropper(false);
            DropColour = null;
            PipetteFull = false;
            dropFilledAt = -1f;
            dropBornAt = Time.time;
            pipetteAxis = Vector3.down;
            pipe = BuildPipette();
            dropper = pipe.root;
            dropper.position = at;
            dropper.localScale = Vector3.one * .05f;
            ToySounds.Play(ToySounds.Cue.Pop, at, .3f, 1.9f);
        }

        // Root at the bulb (between the fingers); the glass hangs along its -Y.
        private Pipette BuildPipette()
        {
            var p = new Pipette();
            p.root = new GameObject("Colour Pipette").transform;
            p.root.SetParent(transform, false);
            p.bulb = ToyPart(PrimitiveType.Sphere, "Bulb", p.root, Rubber, new Vector3(0f, .001f, 0f), new Vector3(BulbRadius * 2f, BulbRadius * 2.5f, BulbRadius * 2f));
            ToyPart(PrimitiveType.Cylinder, "Collar", p.root, RubberDark, new Vector3(0f, -BulbRadius * 1.05f, 0f), new Vector3(PipetteRadius * 2.5f, .0018f, PipetteRadius * 2.5f));
            float top = -BulbRadius * 1.15f, bottom = top - PipetteTube;
            p.liquid = PaintPart(PrimitiveType.Cylinder, "Liquid", p.root);
            p.nozzleLiquid = PaintPart(PrimitiveType.Cylinder, "Nozzle Liquid", p.root);
            p.liquid.localScale = p.nozzleLiquid.localScale = Vector3.zero;
            var holo = Resources.Load<Material>("MakerHologram");
            if (holo != null)
            {
                p.glass = new Material(holo) { name = "Pipette Glass" };
                p.glass.SetColor("_BaseColor", GlassTint);
                if (p.glass.HasProperty("_BaseMap")) p.glass.SetTexture("_BaseMap", Texture2D.whiteTexture);
                // (faint: the paint inside must show its own colour, not a pale wash of it)
                if (p.glass.HasProperty("_Fill")) p.glass.SetFloat("_Fill", .03f);
                if (p.glass.HasProperty("_TexAmount")) p.glass.SetFloat("_TexAmount", 0f);
                if (p.glass.HasProperty("_Rim")) p.glass.SetFloat("_Rim", 1.2f);
                if (p.glass.HasProperty("_RimPower")) p.glass.SetFloat("_RimPower", 3f);
                if (p.glass.HasProperty("_LineAlpha")) p.glass.SetFloat("_LineAlpha", 0f);
                GlassPart("Glass", p.root, p.glass, new Vector3(0f, (top + bottom) * .5f, 0f), new Vector3(PipetteRadius * 2f, PipetteTube * .5f, PipetteRadius * 2f));
                GlassPart("Nozzle", p.root, p.glass, new Vector3(0f, bottom - PipetteNozzle * .5f, 0f), new Vector3(PipetteRadius * .8f, PipetteNozzle * .5f, PipetteRadius * .8f));
            }
            return p;
        }

        private Transform ToyPart(PrimitiveType shape, string name, Transform parent, Color colour, Vector3 at, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(shape);
            Destroy(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", colour);
            block.SetFloat("_VertexColor", 0f);
            r.SetPropertyBlock(block);
            return go.transform;
        }

        private Transform PaintPart(PrimitiveType shape, string name, Transform parent)
        {
            var go = GameObject.CreatePrimitive(shape);
            Destroy(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = PaintBallMaterial ?? material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        private static void GlassPart(string name, Transform parent, Material glass, Vector3 at, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = glass;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        // Where the pipette's tip is (world).
        private static Vector3 PipetteTip(Transform root) =>
            root.TransformPoint(new Vector3(0f, -BulbRadius * 1.15f - PipetteTube - PipetteNozzle, 0f));

        // Every frame while the pinch is held: at the fingers, swaying after the hand; the bulb, the level, the glass.
        private void DropperFrame(Vector3 pinch)
        {
            if (dropper == null || pipe == null) return;
            float now = Time.time;
            Vector3 want = Vector3.down;
            Vector3 toTarget = captureTarget - pinch;
            if (captureOnSurface && toTarget.magnitude < .3f && toTarget.y < -.02f) want = Vector3.Slerp(Vector3.down, toTarget.normalized, .6f);
            else if (Head != null)
            {
                Vector3 away = Vector3.ProjectOnPlane(pinch - Head.position, Vector3.up);
                if (away.sqrMagnitude > 1e-4f) want = (Vector3.down + away.normalized * .3f).normalized;
            }
            // (the hand's own motion swings it: the axis follows with a lag)
            Vector3 moved = pinch - dropper.position;
            want = (want - moved * 6f).normalized;
            pipetteAxis = Vector3.Slerp(pipetteAxis, want, 1f - Mathf.Exp(-Time.deltaTime * 9f));
            dropper.SetPositionAndRotation(pinch, Quaternion.FromToRotation(Vector3.up, -pipetteAxis));
            dropper.localScale = Vector3.one * Mathf.Max(.05f, Overshoot((now - dropBornAt) / .18f));
            PipetteParts(pipe, now);
        }

        private void PipetteParts(Pipette p, float now)
        {
            // the bulb: squeezed (narrow, taller) and let go
            float s = now - p.squeezeAt;
            float squeeze = s < 0f ? 0f : s < .12f ? s / .12f : Mathf.Max(0f, 1f - (s - .12f) / .35f);
            if (p.bulb != null) p.bulb.localScale = new Vector3(BulbRadius * 2f * (1f - .28f * squeeze), BulbRadius * 2.5f * (1f + .12f * squeeze), BulbRadius * 2f * (1f - .28f * squeeze));
            // the paint inside, from the tip up
            float top = -BulbRadius * 1.15f, bottom = top - PipetteTube;
            float level = Mathf.Clamp01(p.fill) * PipetteTube * .94f;
            if (p.liquid != null)
            {
                p.liquid.localScale = level > 1e-4f ? new Vector3(PipetteRadius * 1.56f, level * .5f, PipetteRadius * 1.56f) : Vector3.zero;
                p.liquid.localPosition = new Vector3(0f, bottom + level * .5f, 0f);
            }
            if (p.nozzleLiquid != null)
            {
                p.nozzleLiquid.localScale = p.fill > .02f ? new Vector3(PipetteRadius * .55f, PipetteNozzle * .5f, PipetteRadius * .55f) : Vector3.zero;
                p.nozzleLiquid.localPosition = new Vector3(0f, bottom - PipetteNozzle * .5f, 0f);
            }
        }

        // While the pinch is held: the picture taken at the pinch is in, so the colour is read now (the same way the
        // let-go reads it) and drawn up into the pipette.
        private void LiveSample(Pick p)
        {
            p.sampled = true;
            if (p.frame == null) return;
            var view = PickPoint(p.frame, captureTarget, captureTip, captureWrist, p.fingers);
            var colour = RoomEyes.TrueColourAt(p.frame, view); // F69: the room's light taken out
            if (!colour.HasValue) return;
            p.colour = colour;
            p.view = view;
            FillDropper(colour.Value, captureTarget);
        }

        // The picture is in and the colour read: the pipette sips it.
        private void FillDropper(Color colour, Vector3 from)
        {
            if (dropper == null || pipe == null) return;
            DropColour = colour;
            DropsFilled++;
            dropFilledAt = Time.time;
            pipe.colour = colour;
            pipe.squeezeAt = Time.time;
            if (pipe.liquid != null) TintBall(pipe.liquid, colour);
            if (pipe.nozzleLiquid != null) TintBall(pipe.nozzleLiquid, colour);
            ToySounds.Play(ToySounds.Cue.Drip, dropper.position, .5f, .8f);
            StartCoroutine(Sip(pipe, colour, from));
        }

        // The bulb lets go and the paint comes up: a spiral of paint into the tip (from a wet spot when the surface is
        // close), the level rising; full: the glass takes the colour, a sparkle and a ding.
        private IEnumerator Sip(Pipette p, Color colour, Vector3 from)
        {
            Vector3 tip = PipetteTip(p.root);
            bool close = Vector3.Distance(from, tip) < .3f;
            Transform pool = null;
            if (close)
            {
                Vector3 normal = SurfaceNormal(from);
                pool = ColourBall("Pick Pool", colour, .5f);
                pool.SetPositionAndRotation(from + normal * .001f, Quaternion.FromToRotation(Vector3.up, normal));
                pool.localScale = new Vector3(.022f, .0015f, .022f);
            }
            Vector3 away = Head != null ? (tip - Head.position).normalized : Vector3.forward;
            Vector3 source = close ? from : tip + (from - tip).normalized * .07f + away * .01f;
            for (int i = 0; i < 14; i++) StartCoroutine(SpiralBead(p, colour, source, i * .03f, i * 2.1f));
            ToySounds.Play(ToySounds.Cue.Drip, tip, .45f, 1.1f);
            StartCoroutine(After(.22f, () => ToySounds.Play(ToySounds.Cue.Drip, tip, .4f, 1.35f)));
            for (float t = 0f; t < 1f && p.root != null; t += Time.deltaTime / SipSeconds)
            {
                p.fill = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - .1f) / .9f));
                if (pool != null) pool.localScale = new Vector3(.022f, .0015f, .022f) * (1f - t * t);
                yield return null;
            }
            if (pool != null) Destroy(pool.gameObject);
            if (p.root == null || p != pipe) yield break; // (let go meanwhile: PipetteAway empties it)
            p.fill = 1f;
            p.full = true;
            PipetteFull = true;
            if (p.glass != null) p.glass.SetColor("_BaseColor", Color.Lerp(GlassTint, colour, .35f));
            Vector3 glassAt = p.root.TransformPoint(new Vector3(0f, -BulbRadius * 1.15f - PipetteTube * .5f, 0f));
            ToySounds.Play(ToySounds.Cue.Ding, glassAt, .28f, 1.3f);
            StartCoroutine(Twinkle(glassAt + (Head != null ? (Head.position - glassAt).normalized * .012f : Vector3.zero) + p.root.right * .012f));
        }

        // One bead of paint spiralling up into the tip (the tip followed as the hand moves).
        private IEnumerator SpiralBead(Pipette p, Color colour, Vector3 source, float delay, float phase)
        {
            yield return new WaitForSeconds(delay);
            if (p.root == null) yield break;
            var bead = ColourBall("Pick Bead", colour, .0024f);
            for (float u = 0f; u < 1f && bead != null && p.root != null; u += Time.deltaTime / .32f)
            {
                Vector3 tip = PipetteTip(p.root);
                Vector3 line = tip - source;
                Vector3 a = Vector3.Cross(line, Vector3.up);
                if (a.sqrMagnitude < 1e-8f) a = Vector3.right;
                a.Normalize();
                Vector3 b = Vector3.Cross(line.normalized, a);
                float e = u * u * (3f - 2f * u);
                float radius = .011f * (1f - e);
                float angle = phase + u * Mathf.PI * 4f;
                bead.position = Vector3.Lerp(source, tip, e) + (a * Mathf.Cos(angle) + b * Mathf.Sin(angle)) * radius;
                bead.localScale = Vector3.one * Mathf.Lerp(.0048f, .0022f, e);
                yield return null;
            }
            if (bead != null) Destroy(bead.gameObject);
        }

        // A small four-pointed sparkle that twinkles and goes.
        private Mesh pipetteSparkle;
        private IEnumerator Twinkle(Vector3 at)
        {
            if (pipetteSparkle == null) { pipetteSparkle = Sparkle(new Color(1f, .99f, .9f), new Color(1f, .82f, .4f)); owned.Add(pipetteSparkle); }
            var go = new GameObject("Pipette Sparkle", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(transform, false);
            go.GetComponent<MeshFilter>().sharedMesh = pipetteSparkle;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            for (float t = 0f; t < 1f && go != null; t += Time.deltaTime / .55f)
            {
                go.transform.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at) * Quaternion.Euler(0f, 0f, t * 120f));
                go.transform.localScale = Vector3.one * Mathf.Sin(t * Mathf.PI) * 1.6f;
                yield return null;
            }
            if (go != null) Destroy(go);
        }

        // Let go: the bulb squeezes a drop out of the tip; the drop is handed over (it flies into the palette) and
        // the pipette shrinks away.
        private Transform TakeDropper()
        {
            var p = pipe;
            var root = dropper;
            dropper = null;
            pipe = null;
            if (root == null || p == null) return null;
            Vector3 tip = PipetteTip(root);
            Color c = DropColour ?? new Color(.9f, .93f, .96f);
            var drop = ColourBall("Colour Drop", c, DropRadius);
            drop.position = tip - root.up * DropRadius * .8f;
            drop.localScale = Vector3.one * DropRadius * 2f * (DropColour.HasValue ? 1f : .5f);
            p.squeezeAt = Time.time;
            ToySounds.Play(ToySounds.Cue.Splat, tip, .3f, 1.8f);
            StartCoroutine(PipetteAway(p, true));
            return drop;
        }

        private void EndDropper(bool pop)
        {
            var p = pipe;
            var root = dropper;
            dropper = null;
            pipe = null;
            if (root == null || p == null) return;
            if (pop)
            {
                ToyFx.Puff(root.position, .006f, 2);
                ToySounds.Play(ToySounds.Cue.Pop, root.position, .3f, 2.2f);
            }
            StartCoroutine(PipetteAway(p, false));
        }

        // The pipette empties (squeezed: the level drops) and shrinks into a puff.
        private IEnumerator PipetteAway(Pipette p, bool squeezed)
        {
            float fill = p.fill;
            if (!squeezed) p.fill = 0f;
            Vector3 scale = p.root != null ? p.root.localScale : Vector3.one;
            float seconds = squeezed ? .38f : .18f;
            for (float t = 0f; t < 1f && p.root != null; t += Time.deltaTime / seconds)
            {
                if (squeezed) p.fill = fill * (1f - Mathf.Clamp01(t * 1.6f));
                PipetteParts(p, Time.time);
                float k = Mathf.Clamp01((t - (squeezed ? .45f : 0f)) / (squeezed ? .55f : 1f));
                p.root.localScale = scale * (1f - k * k);
                yield return null;
            }
            if (p.root != null)
            {
                if (squeezed) ToyFx.Puff(p.root.position, .005f, 2);
                Destroy(p.root.gameObject);
            }
            if (p.glass != null) Destroy(p.glass);
        }

        private void TintBall(Transform ball, Color colour)
        {
            var r = ball != null ? ball.GetComponent<Renderer>() : null;
            if (r == null) return;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", colour);
            block.SetFloat("_VertexColor", 0f);
            r.SetPropertyBlock(block);
        }

        // A 96 px crop of the pick's picture around the spot, the spot crossed in yellow and the colour taken as a
        // strip along the bottom: persistentDataPath/eyes/pick_*.jpg.
        // F69: the strip's left half is what the camera had there (`seen`), the right half the colour kept.
        public Color? LastPickSeen { get; private set; }
        private void SavePickCrop(RoomEyes.Frame frame, Vector2 view, Color colour, Color? seen = null)
        {
            if (frame?.pixels == null) return;
            const int side = 96, half = side / 2;
            int w = frame.size.x, h = frame.size.y;
            int cx = Mathf.RoundToInt(view.x * w), cy = Mathf.RoundToInt(view.y * h);
            var crop = new Color32[side * side];
            Color32 mark = new Color32(255, 220, 0, 255), taken = colour, camera = seen ?? colour;
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    int sx = Mathf.Clamp(cx - half + x, 0, w - 1), sy = Mathf.Clamp(cy - half + y, 0, h - 1);
                    Color32 c = frame.pixels[sy * w + sx];
                    bool cross = (x == half || y == half) && Mathf.Abs(x - half) + Mathf.Abs(y - half) > 4 && Mathf.Abs(x - half) + Mathf.Abs(y - half) < 16;
                    if (y < 10) c = x < half ? camera : taken;
                    else if (cross) c = mark;
                    crop[y * side + x] = c;
                }
            string folder = Path.Combine(Application.persistentDataPath, "eyes");
            string file = Path.Combine(folder, "pick_" + DateTime.Now.ToString("MMdd-HHmmss-fff") + ".jpg");
            LastPickCrop = file;
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllBytes(file, RoomEyes.Jpeg(crop, side, side, 88));
                }
                catch (Exception) { }
            });
        }
    }
}
