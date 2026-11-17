using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // F70 (2026-11-14, after the Quest try: the paint looked bumpy, like brushed paint, and went on "little by
    // little", as if the strokes had to be precise): one stroke paints the whole part. Where the paint-loaded
    // fingertip touches a part, the colour floods out from that spot over all of that part (PaintFill: about
    // FillSpeed, between FillShortest and FillLongest), a clean flat colour with a thin light line at its edge;
    // then the part has the paint. There are no smears, flecks or shine any more (they were the bumps).
    // A stroke stays on the part it started on (sliding over an edge does not paint the next face; lift the
    // finger for another part). The eraser (on the thumb of the painting hand, or the index when the thumb holds
    // the paint; MakerSession.Brush.cs) works the same way in reverse: a short rub (EraseAfter on the part, so a
    // graze of the thumb beside the painting finger does nothing) and the bare part spreads from that spot over
    // the paint. "Undo" takes back the last part painted or erased (or the fill going on).
    public sealed partial class MakerSession
    {
        public const float FillSpeed = .5f, FillShortest = .22f, FillLongest = .5f, EraseAfter = .12f;
        public float LastStrokeCover { get; private set; }   // how far the latest fill has spread (0-1)
        public int Fills { get; private set; }               // fills started (paint and eraser)
        public int Erased { get; private set; }
        public int Undone { get; private set; }
        public int PaintTouches { get; private set; }   // F49: paint fills started on made things (the finish lesson's "rubbed it")
        public int EraseTouches { get; private set; }   // F49: paint the eraser took off (or a fill it stopped)
        public static readonly Swatch Eraser = new Swatch { kind = "eraser", name = "eraser", hex = "#f3b7b4" };

        // One fingertip's stroke: the part it is on, when it last touched, and its tip this frame.
        private sealed class Tool
        {
            public readonly bool erases;
            public PaintableThing thing;
            public int part = -1;
            public float at = -10f, since = -10f;   // last touch; when this touch on the part began
            public Vector3? tip;
            public Tool(bool erases) { this.erases = erases; }
            public void Reset() { thing = null; part = -1; at = -10f; }
        }

        // A part being flooded: the new paint spreading over it (drawn over the part with a copy of the paint's
        // material), or, for the eraser, the old paint drawn only outside a growing circle over the bare part.
        private sealed class Fill
        {
            public PaintableThing thing;
            public int part;
            public bool erases, stopped;
            public Swatch swatch;
            public Material overlay;
            public Vector3 at;
            public float start, duration, reach;
        }

        private sealed class PaintStep { public PaintableThing thing; public int part; public Material before; public PaintableThing.Mark mark; }

        private readonly List<Fill> fills = new List<Fill>();
        private readonly List<PaintStep> paintHistory = new List<PaintStep>();
        private readonly Tool paintTool = new Tool(false), eraseTool = new Tool(true);
        private Mesh eraserMesh, sleeveMesh;

        // While a stroke goes on (touched in the last 0.3 s), only its own part is brushed.
        private bool StrokeLocked(Tool tool, PaintableThing thing, int part)
        {
            if (tool.thing == null || Time.time - tool.at > .3f) return false;
            if (tool.thing == thing && tool.part == part) return false;
            // Touching another part: carry on on the stroke's part if the fingertip is still on it.
            if (tool.tip.HasValue && tool.thing.ClosestOnPart(tool.part, tool.tip.Value, out var surface, out var normal, out float distance) && distance <= TouchReach)
                Stroke(tool, tool.thing, tool.part, surface, normal);
            return true;
        }

        // The brush's (or the eraser's) fingertip touches `part` at `surface` (the nearest point on it).
        private void Stroke(Tool tool, PaintableThing thing, int part, Vector3 surface, Vector3 normal)
        {
            brushUsedAt = Time.time;
            if (tool.thing != thing || tool.part != part || Time.time - tool.at > .3f) tool.since = Time.time;
            tool.thing = thing;
            tool.part = part;
            tool.at = Time.time;
            if (tool.erases)
            {
                // One graze of the thumb (it rides beside the painting finger) must not wipe a whole part: the
                // eraser takes the paint off after a short rub on it.
                if (Time.time - tool.since >= EraseAfter) Erase(thing, part, surface);
                return;
            }
            var swatch = brushSwatch;
            if (thing.Marks.TryGetValue(part, out var was) && was.hex == swatch.hex && was.image == swatch.image) return; // already this paint
            var running = FillOn(thing, part);
            if (running != null && !running.erases && running.swatch == swatch) return; // already flooding with it
            if (running != null) StopFill(running);
            PaintTouches++;
            ToySounds.Play(ToySounds.Cue.Splat, surface, .3f, 1.5f);
            ToySounds.Play(ToySounds.Cue.Brush, surface, .75f, Random.Range(.9f, 1.05f));
            StartFill(thing, part, surface, SwatchMaterial(swatch.kind, swatch.hex, swatch.image), false, swatch);
        }

        // Flooded: the part takes the paint.
        private void CompletePart(PaintableThing thing, int part, Vector3 at, Swatch swatch)
        {
            Remember(thing, part);
            thing.Apply(part, SwatchMaterial(swatch.kind, swatch.hex, swatch.image), new PaintableThing.Mark { kind = swatch.kind, hex = swatch.hex, image = swatch.image });
            PaintStrokes++;
            Heard();
            if (!eraserHinted && eraser != null)
            {
                // Once: where the eraser is.
                eraserHinted = true;
                Caption(Crew.Spark, eraserFinger == HandJointId.HandThumbTip ? "Your thumb rubs it off!" : "Your index rubs it off!", 2.5f);
            }
            ToySounds.Play(ToySounds.Cue.Chime, at, .25f, 2.1f);
            var root = MadeRootOf(thing.transform);
            Talk.Add("Person painted the " + thing.Parts[part] + " of " + (root != null ? root.name : "it") + " with " + swatch.name);
            RememberPaint(root);
        }

        // ------------------------------------------------------------ the fill

        private Fill FillOn(PaintableThing thing, int part)
        {
            foreach (var f in fills) if (f.thing == thing && f.part == part && !f.stopped) return f;
            return null;
        }

        // Starts the flood over `part` from `at`. `look` is the paint spreading in (or, erasing, the paint going
        // away); a look that is not a Windups/True Colour material just swaps at the end.
        private void StartFill(PaintableThing thing, int part, Vector3 at, Material look, bool erases, Swatch swatch)
        {
            var fill = new Fill { thing = thing, part = part, erases = erases, swatch = swatch, at = at, start = Time.time };
            var mesh = thing.Mesh;
            if (look != null && look.HasProperty("_FillMode") && mesh != null && part >= 0 && part < mesh.subMeshCount)
            {
                // Distances in metres along the object's own axes (the shader measures the same way).
                Vector3 s = thing.transform.lossyScale;
                Vector3 scale = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                Vector3 centre = Vector3.Scale(thing.transform.InverseTransformPoint(at), scale);
                var b = mesh.GetSubMesh(part).bounds;
                float reach = 0f;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + new Vector3((i & 1) == 0 ? -b.extents.x : b.extents.x, (i & 2) == 0 ? -b.extents.y : b.extents.y, (i & 4) == 0 ? -b.extents.z : b.extents.z);
                    reach = Mathf.Max(reach, Vector3.Distance(Vector3.Scale(corner, scale), centre));
                }
                fill.reach = reach + .003f;
                fill.duration = Mathf.Clamp(reach / FillSpeed, FillShortest, FillLongest);
                fill.overlay = new Material(look) { name = look.name + " (fill)" };
                fill.overlay.EnableKeyword("_FILL");
                fill.overlay.SetFloat("_FillMode", erases ? 2f : 1f);
                fill.overlay.SetVector("_FillCentre", centre);
                fill.overlay.SetFloat("_FillRadius", 0f);
                fill.overlay.SetFloat("_DepthOffset", -1f);
            }
            fills.Add(fill);
            Fills++;
            LastStrokeCover = 0f;
            StartCoroutine(PaintFill(fill));
        }

        private IEnumerator PaintFill(Fill fill)
        {
            while (!fill.stopped && fill.thing != null && fill.overlay != null)
            {
                float t = (Time.time - fill.start) / Mathf.Max(.01f, fill.duration);
                LastStrokeCover = Mathf.Clamp01(t);
                var mesh = fill.thing.Mesh;
                if (t >= 1f || mesh == null || fill.part >= mesh.subMeshCount) break;
                float grow = 1f - (1f - t) * (1f - t);
                fill.overlay.SetFloat("_FillRadius", Mathf.Lerp(.008f, fill.reach, grow));
                var renderer = fill.thing.Renderer;
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                    Graphics.DrawMesh(mesh, fill.thing.transform.localToWorldMatrix, fill.overlay, fill.thing.gameObject.layer, null, fill.part, null, ShadowCastingMode.Off, false);
                yield return null;
            }
            fills.Remove(fill);
            if (fill.overlay != null) Destroy(fill.overlay);
            fill.overlay = null;
            LastStrokeCover = 1f;
            if (fill.stopped || fill.thing == null || fill.erases) yield break;
            CompletePart(fill.thing, fill.part, fill.at, fill.swatch);
        }

        private void StopFill(Fill fill)
        {
            fill.stopped = true;
            fills.Remove(fill);
        }

        // ------------------------------------------------------------ the eraser

        private void Erase(PaintableThing thing, int part, Vector3 surface)
        {
            var running = FillOn(thing, part);
            if (running != null && !running.erases)
            {
                // Paint still flooding in is wiped away at once.
                StopFill(running);
                EraseTouches++;
                ToySounds.Play(ToySounds.Cue.Brush, surface, .6f, .66f);
                return;
            }
            if (running != null || !thing.Marks.ContainsKey(part)) return; // already going, or nothing to rub off
            EraseTouches++;
            var paint = thing.Current(part);
            Remember(thing, part);
            thing.Clear(part);
            StartFill(thing, part, surface, paint, true, Eraser);
            Erased++;
            Heard();
            ToySounds.Play(ToySounds.Cue.Brush, surface, .6f, .66f);
            ToySounds.Play(ToySounds.Cue.Pop, surface, .3f, 1.2f);
            var root = MadeRootOf(thing.transform);
            Talk.Add("Person rubbed the paint off the " + thing.Parts[part] + " of " + (root != null ? root.name : "it"));
            RememberPaint(root);
        }

        // ------------------------------------------------------------ undo

        private void Remember(PaintableThing thing, int part)
        {
            thing.Marks.TryGetValue(part, out var mark);
            paintHistory.Add(new PaintStep { thing = thing, part = part, before = thing.Current(part), mark = mark });
            if (paintHistory.Count > 30) paintHistory.RemoveAt(0);
        }

        // Takes back the paint still flooding in, else the last part painted or erased.
        public bool UndoPaint()
        {
            for (int i = fills.Count - 1; i >= 0; i--)
            {
                if (fills[i].erases) continue;
                StopFill(fills[i]);
                Undone++;
                return true;
            }
            while (paintHistory.Count > 0)
            {
                var step = paintHistory[paintHistory.Count - 1];
                paintHistory.RemoveAt(paintHistory.Count - 1);
                if (step.thing == null) continue;
                var going = FillOn(step.thing, step.part);
                if (going != null) StopFill(going);
                if (step.mark == null) step.thing.Clear(step.part);
                else step.thing.Apply(step.part, step.before, new PaintableThing.Mark { kind = step.mark.kind, hex = step.mark.hex, image = step.mark.image });
                Undone++;
                Heard();
                var at = step.thing.Renderer != null ? step.thing.Renderer.bounds.center : transform.position;
                ToyFx.Puff(at, .01f, 3);
                ToySounds.Play(ToySounds.Cue.Whoosh, at, .35f, 1.6f);
                var root = MadeRootOf(step.thing.transform);
                Talk.Add("Person undid the paint on the " + step.thing.Parts[step.part]);
                RememberPaint(root);
                return true;
            }
            return false;
        }

        // "undo"; "eraser" only says where the eraser is (it comes with the paint, on the thumb).
        private bool PaintVoice(string p)
        {
            bool undo = p == "undo" || p.StartsWith("undo") || p.Contains("undo that") || p.Contains("되돌") || p.Contains("실행 취소") || p == "취소";
            if (undo && !Drawing)
            {
                if (UndoPaint()) { Ack(Crew.Spark, "Undone!"); return true; }
                return false;
            }
            if (!Drawing && (p.Contains("eraser") || p.Contains("rub it off") || p.Contains("지우개")))
            {
                Heard();
                if (eraser == null) Caption(Crew.Spark, "Dip a finger in a colour first", 2.5f);
                else Caption(Crew.Spark, eraserFinger == HandJointId.HandThumbTip ? "Rub with your thumb!" : "Rub with your index finger!", 2.5f);
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------ the eraser's look

        // The eraser on the fingertip: a pink block in a blue sleeve, capping the finger's end (its +Z points
        // out along the finger, like the brush's).
        private Transform EraserTip()
        {
            var tip = new GameObject("Eraser").transform;
            tip.SetParent(transform, false);
            if (eraserMesh == null)
            {
                Color pink = Hex(Eraser.hex), blue = new Color(.32f, .52f, .92f);
                eraserMesh = ToyMeshes.RoundedSlab(.012f, .009f, .003f, .01f, pink, Color.Lerp(pink, Color.black, .12f), pink, "EraserTip");
                sleeveMesh = ToyMeshes.RoundedSlab(.0134f, .0104f, .0036f, .0055f, blue, new Color(.22f, .38f, .75f), blue, "EraserSleeve");
                owned.Add(eraserMesh);
                owned.Add(sleeveMesh);
            }
            // The block reaches 5 mm past the tracked tip (which sits inside the real finger's end); the sleeve
            // wraps its back half.
            var block = Part("Block", eraserMesh);
            block.SetParent(tip, false);
            block.localPosition = new Vector3(0f, 0f, -.005f);
            var sleeve = Part("Sleeve", sleeveMesh);
            sleeve.SetParent(tip, false);
            sleeve.localPosition = new Vector3(0f, 0f, -.0065f);
            return tip;
        }
    }
}
