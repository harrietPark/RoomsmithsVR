using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Windups.Handoff
{
    // F30: the crew makes more than coasters and vessels.
    //  - What it is comes from the words (`KindFor`) or the picked idea: a tray for the things laid on the
    //    desk (MakerSession.Trace.cs), a clip on the real desk edge (hook, cable clip, little shelf), or any
    //    other object, made by Meshy from the chosen picture (`ModelPlan`).
    //  - Trays and clips are built by the app (FitShapes); Fixer checks them like the coaster (gap around the
    //    pockets, grip on the edge, bar thickness), all computed here, not by the AI.
    //  - It is built right where it will live (no see-through preview since 2026-11-11): the tray around the
    //    real things, the clip on the desk edge (it only slides along the edge when grabbed, and stays
    //    there), the model on the desk. Fixer's check is MakerSession.FixDesign.
    public sealed partial class MakerSession
    {
        public string KindOverride { get; private set; }
        public Quaternion MugTurn { get; private set; } = Quaternion.identity; // the made thing's turn on its spot
        public bool HasEdge { get; private set; }
        public Vector3 EdgeOut { get; private set; }        // clip: out of the desk's front face, toward the person
        public Vector3 EdgeAlong { get; private set; }
        public string ClipWanted { get; private set; }      // hook | cable | shelf, from the words
        public byte[] LookJpeg { get; private set; }        // the chosen picture (what Meshy builds from)
        public bool Modelling { get; private set; }
        public int ModelsMade { get; private set; }
        public string ModelError { get; private set; }
        public static string ModelFolder => Path.Combine(Application.persistentDataPath, "models");

        private bool MeshyReady => Scripted || MeshyClient.Available;
        private bool warnedMeshy;
        private readonly Dictionary<Transform, (Vector3 point, Vector3 along, Quaternion turn)> rails = new Dictionary<Transform, (Vector3, Vector3, Quaternion)>();
        private readonly Dictionary<string, Material> modelMaterials = new Dictionary<string, Material>();

        // ------------------------------------------------------------ what it is

        private static readonly string[] DrinkThings = { "mug", "cup", "glass", "bottle", "tumbler", "can" };

        public static string KindFor(string words)
        {
            string w = (words ?? "").ToLowerInvariant();
            bool Has(params string[] keys) => keys.Any(w.Contains);
            if (Has("tray", "organiser", "organizer", "valet", "catch-all", "catchall", "트레이", "정리함", "쟁반")) return "tray";
            if (Has("hook", "hanger", "clip", "clamp", "cable", "cord", "걸이", "고리", "클립", "케이블", "선 정리")) return "clip";
            if (Has("coaster", "cup mat", "mug mat", "코스터", "컵받침", "컵 받침", "받침")) return "coaster";
            if (Regex.IsMatch(w, @"\bstands?\b|\bdock\b") || Has("거치대", "스탠드")) return "model";
            string noun = ObjectNoun(w);
            if (Has("holder", "dish", "place for", "home for", "spot for") && noun != null && new[] { "keys", "watch", "remote", "charger" }.Contains(noun)) return "tray";
            if (Has("holder") && noun != null && new[] { "phone", "tablet", "headphones", "controller", "speaker", "laptop" }.Contains(noun)) return "model";
            if (ProfileThing(w)) return "vessel";
            if (noun != null && DrinkThings.Contains(noun)) return "coaster";
            return w.Trim().Length == 0 ? "coaster" : "model";
        }

        public static string ClipTypeFor(string words)
        {
            string w = (words ?? "").ToLowerInvariant();
            if (new[] { "cable", "cord", "wire", "charger", "케이블", "선 정리", "전선" }.Any(w.Contains)) return "cable";
            if (new[] { "shelf", "ledge", "선반" }.Any(w.Contains)) return "shelf";
            if (new[] { "hook", "hang", "headphone", "bag", "걸이", "고리", "헤드폰", "가방" }.Any(w.Contains)) return "hook";
            return null;
        }

        private void ResetShapes()
        {
            KindOverride = null;
            MugTurn = Quaternion.identity;
            HasEdge = false;
            ClipWanted = null;
            LookJpeg = null;
            Modelling = false;
            ModelError = null;
            ClearTrace();
        }

        private void WarnNoMeshy()
        {
            if (warnedMeshy) return;
            warnedMeshy = true;
            Say(Crew.Fixer, "The 3D builder isn't set up yet, so I can only make coasters, cups, trays and desk hooks.");
        }

        // ------------------------------------------------------------ measuring, per kind

        private bool StartShapeMeasuring()
        {
            string kind = Kind;
            if (kind != "tray" && kind != "clip" && kind != "model") return false;
            Current = Step.Measuring;
            HasMeasure = false;
            Hint = 0f;
            Tape.Reset();
            ShowTape(false);
            freeTape = false;
            if (kind == "tray") { StartTrace(); return true; }
            if (kind == "clip")
            {
                ClipWanted = ClipTypeFor(Need) ?? ClipWanted;
                if (!HasEdge && !FindEdge()) Say(Crew.Spark, "I can't find the desk's edge. Face the desk and I'll look again.");
                Hint = 25f;
                // F41: a snug clip needs the real thickness: a ruler is best, the hands are a few millimetres off.
                Say(Crew.Fixer, "How thick is your desk top? A ruler is best: just say the number. Or pinch its top and bottom.");
                TapeLessonOrDemo(); // F49: the step-by-step tape lesson (the old demo when FormFirst is off)
                Say(Crew.Spark, "Most desks are about 25 millimetres. Say use yours if that looks right.");
                return true;
            }
            if (!MeshyReady) WarnNoMeshy();
            PlaceModelSpot();
            // F61: why first, then the sizes this object needs, one by one (MakerSession.Guide.cs). (F39 asked one
            // size: the height of a tall thing, else its width.)
            StartSizePlan();
            return true;
        }

        // Where the measuring does not move the spot: the clip stays on the edge, the model where it was put.
        private bool KeepsSpot() => Kind == "clip" || Kind == "model";

        // After "okay" on the measure: the clip asks its type (unless the words said it), the model goes
        // straight to Meshy. Trays confirm their traced pockets in MakerSession.Trace.cs.
        private bool AfterMeasure()
        {
            string kind = Kind;
            if (kind == "clip")
            {
                Current = Step.Shape;
                if (!string.IsNullOrEmpty(ClipWanted)) { Shape = ClipWanted; StartCoroutine(Plan("")); }
                else AskWith(Crew.Maker, "A hook, a cable clip, or a little shelf?", false, Rp("Hook"), Rp("Cable clip"), Rp("Little shelf"));
                return true;
            }
            if (kind == "model")
            {
                Current = Step.Shape;
                Shape = "";
                StartCoroutine(Plan(""));
                return true;
            }
            return false;
        }

        // The model stands on the desk in front, beside the real thing it is for.
        private void PlaceModelSpot()
        {
            Vector3 spot = DeskPointInFront();
            if (Target != null && Target.onSurface && Head != null)
            {
                Vector3 toUser = Vector3.ProjectOnPlane(Head.position - Target.basePoint, Vector3.up).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, toUser);
                spot = Target.basePoint - side * (Target.width * .5f + .07f);
                spot.y = DeskHeightAt(spot, Target.basePoint.y);
            }
            MugSpot = spot;
            HasSpot = true;
            Vector3 face = Head != null ? Vector3.ProjectOnPlane(Head.position - spot, Vector3.up) : Vector3.back;
            MugTurn = Quaternion.LookRotation(face.sqrMagnitude > 1e-6f ? face.normalized : Vector3.back, Vector3.up);
        }

        // ------------------------------------------------------------ the desk edge

        // The real desk's front edge nearest the person (MRUK geometry): from the desk point in front, walk
        // toward the person until the desk top ends, at three points across; that gives the edge and its
        // direction. No state changes.
        public bool ProbeEdge(out Vector3 point, out Vector3 outward)
        {
            point = outward = Vector3.zero;
            if (Head == null || arrival == null) return false;
            int mask = arrival.handoff.meeting.environment.GeometryMask;
            Vector3 desk = DeskPointInFront();
            Vector3 toUser = Vector3.ProjectOnPlane(Head.position - desk, Vector3.up);
            if (toUser.sqrMagnitude < 1e-4f) return false;
            toUser.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, toUser);
            var points = new List<Vector3>();
            foreach (float across in new[] { -.08f, 0f, .08f })
            {
                Vector3 start = desk + side * across;
                if (!Physics.Raycast(start + Vector3.up * .3f, Vector3.down, out var hit, .6f, mask, QueryTriggerInteraction.Ignore) || hit.normal.y < .9f) continue;
                float top = hit.point.y;
                bool OnTop(float d)
                {
                    Vector3 p = start + toUser * d;
                    return Physics.Raycast(new Vector3(p.x, top + .05f, p.z), Vector3.down, out var h, .1f, mask, QueryTriggerInteraction.Ignore) && Mathf.Abs(h.point.y - top) < .012f;
                }
                float inside = 0f, outside = -1f;
                for (float d = .005f; d < .8f; d += .005f)
                {
                    if (OnTop(d)) inside = d;
                    else { outside = d; break; }
                }
                if (outside < 0f) continue;
                for (int k = 0; k < 8; k++) { float m = (inside + outside) * .5f; if (OnTop(m)) inside = m; else outside = m; }
                points.Add(new Vector3(start.x, top, start.z) + toUser * inside);
            }
            if (points.Count == 0) return false;
            point = points.Count == 3 ? points[1] : points[0];
            Vector3 along = points.Count >= 2 ? Vector3.ProjectOnPlane(points[points.Count - 1] - points[0], Vector3.up) : side;
            if (along.sqrMagnitude < 1e-6f) along = side;
            outward = Vector3.Cross(along.normalized, Vector3.up);
            if (Vector3.Dot(outward, toUser) < 0f) outward = -outward;
            return true;
        }

        private bool FindEdge()
        {
            if (!ProbeEdge(out var point, out var outward)) return false;
            SetEdge(point, outward);
            return true;
        }

        private void SetEdge(Vector3 point, Vector3 outward)
        {
            EdgeOut = outward.normalized;
            EdgeAlong = Vector3.Cross(EdgeOut, Vector3.up).normalized;
            MugTurn = Quaternion.LookRotation(EdgeAlong, Vector3.up); // its right is EdgeOut
            MugSpot = point;
            HasSpot = true;
            HasEdge = true;
        }

        // ------------------------------------------------------------ plans

        private IEnumerator ClipPlan(string change)
        {
            Current = Step.Planning;
            Hush();
            Ack(Crew.Maker, change.Length > 0 ? "On it!" : "Ooh, nice.");
            yield return new WaitForSeconds(Scripted ? .3f : .8f);
            if (!HasEdge && !FindEdge()) { Say(Crew.Spark, "I lost the desk edge. Look at it and say that again."); pendingChange = null; Current = Product != null ? Step.Done : Step.Shape; yield break; }
            string w = (Shape + " " + change).ToLowerInvariant();
            bool fresh = Design == null || Design.kind != "clip";
            // F41: the last print's "too tight / too loose" moves a new clip's grip gap too.
            var d = fresh ? new CoasterDesign { kind = "clip", clearance = .8f + Mathf.Clamp(fitBias, -.4f, .8f), rimWidth = 4.5f, grip = 35f } : Design.Copy();
            string type = ClipTypeFor(w) ?? (fresh ? ClipWanted ?? "hook" : d.clipType);
            if (fresh || type != d.clipType)
            {
                d.clipType = type;
                d.rimHeight = type == "hook" ? 40f : type == "cable" ? 7f : 60f;
                d.width = type == "hook" ? 22f : type == "cable" ? 14f : 80f;
            }
            d.cupDiameter = HasMeasure ? Measured : 25f;
            if (w.Contains("wider") || w.Contains("넓")) d.width *= 1.3f;
            if (w.Contains("narrower") || w.Contains("slimmer") || w.Contains("좁")) d.width /= 1.3f;
            if (w.Contains("longer") || w.Contains("deeper") || w.Contains("bigger") || w.Contains("길")) d.rimHeight *= 1.25f;
            if (w.Contains("shorter") || w.Contains("smaller") || w.Contains("짧")) d.rimHeight /= 1.25f;
            if (w.Contains("thicker") || w.Contains("sturd") || w.Contains("stronger") || w.Contains("두껍")) d.rimWidth += 1f;
            if (w.Contains("looser") || w.Contains("헐렁")) d.clearance += .4f;
            if (w.Contains("tighter") || w.Contains("snug") || w.Contains("빡빡")) d.clearance -= .3f;
            d.name = Look != null ? MakerRoles.Clean(Look.label) : type == "hook" ? "Desk hook" : type == "cable" ? "Cable clip" : "Desk shelf";
            if (string.IsNullOrEmpty(d.color)) d.color = ClipColour(); // F41: the colours the person gave
            d.why = "";
            Design = d.Clamped();
            SetEdge(MugSpot, EdgeOut);
            Note("design", Design.name, Design.Summary);
            Say(Crew.Maker, type == "hook" ? "A hook that clamps your desk edge. Making it!" : type == "cable" ? "A cable clip for your desk edge. Making it!" : "A little shelf on your desk edge. Making it!");
            yield return FitAndBuild();
        }

        private static bool SizeChange(string w, out float factor, out float mm)
        {
            factor = 1f;
            mm = 0f;
            var size = Regex.Match(w, @"(\d+(?:\.\d+)?)\s*(mm|cm|millimet|centimet|밀리|센티)");
            if (size.Success)
            {
                mm = float.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * (size.Groups[2].Value.StartsWith("c") || size.Groups[2].Value.StartsWith("센") ? 10f : 1f);
                return true;
            }
            if (Regex.IsMatch(w, @"^(make it |a bit |a little |much )*(bigger|larger|wider|smaller|narrower|tinier)( please)?$") ||
                w == "크게" || w == "작게" || w == "더 크게" || w == "더 작게")
            {
                factor = w.Contains("bigger") || w.Contains("larger") || w.Contains("wider") || w.Contains("크") ? 1.2f : 1f / 1.2f;
                return true;
            }
            return false;
        }

        // Any other object: Meshy builds it from the chosen picture (changed by the words, if any).
        private IEnumerator ModelPlan(string change)
        {
            Current = Step.Planning;
            Hush();
            if (!MeshyReady) { warnedMeshy = false; WarnNoMeshy(); pendingChange = null; Current = Product != null ? Step.Done : Step.Shape; yield break; }
            string w = (change ?? "").ToLowerInvariant().Trim().TrimEnd('.', '!');
            if (!HasSpot) PlaceModelSpot();
            if (!w.StartsWith("use the measured ") && draftFrom == null && Design != null && Design.kind == "model" && !string.IsNullOrEmpty(Design.model) && SizeChange(w, out float factor, out float mm))
            {
                Ack(Crew.Maker, "On it!");
                var d = Design.Copy();
                float ratio = d.modelHeight / Mathf.Max(1f, d.cupDiameter);
                d.cupDiameter = mm > 0f ? mm : d.cupDiameter * factor;
                d.modelHeight = d.cupDiameter * ratio;
                Design = d.Clamped();
                Note("design", Design.name, Design.Summary);
                Say(Crew.Maker, $"Now {Design.cupDiameter:0} millimetres wide.");
                yield return FitAndBuild();
                yield break;
            }
            Ack(Crew.Maker, "On it!");
            byte[] picture = LookJpeg;
            if (w.Length > 0 && picture != null && !Scripted && !revisionPrepared)
            {
                Say(Crew.Maker, "Let me redraw it first.");
                yield return EditPicture(picture, change, r => picture = r);
                if (picture == null) { Current = Product != null ? Step.Done : Step.Shape; RevisionFailed(); yield break; }
                if (Current != Step.Planning) yield break;
            }
            if (FormFirst && !placementApproved)
            {
                yield return ChoosePlacement(versionSpot ?? (Product != null ? Product.position : MugSpot), versionSpot.HasValue ? versionTurn : MugTurn, Mathf.Max(40f, Measured) * .0005f);
                if (!placementApproved) { Current = Product != null ? Step.Done : Step.Shape; RevisionFailed(); yield break; }
                if (Product == null) { MugSpot = approvedPlace; MugTurn = approvedTurn; }
                else { versionSpot = approvedPlace; versionTurn = approvedTurn; }
            }
            Modelling = true;
            ModelError = null;
            working.Add(Crew.Maker);
            // F33: two Meshy stages (the shape in seconds, then its colours), and Maker makes it in front of
            // the person meanwhile: clay, the shape, painting, the reveal (MakerSession.Sculpt.cs).
            // F39: the key size is a width, or a height (tall things: the width follows the shape's proportions).
            // F40: v2 from the draft keeps v1's width (sizes changed on the draft are applied after).
            var fromDraft = draftFrom;
            float widthMm = fromDraft != null ? fromDraft.cupDiameter : HasMeasure ? (KeyAxis == "H" ? Measured * .65f : Measured) : 100f;
            float KeyWidth(Mesh m) => fromDraft != null ? fromDraft.cupDiameter : HasMeasure && KeyAxis == "H" ? WidthFromKey(m, Measured) : widthMm;
            Vector3 spot = versionSpot ?? (Product != null ? Product.position : MugSpot + Vector3.up * .0005f);
            Quaternion turn = versionSpot != null ? versionTurn : Product != null ? Product.rotation : MugTurn;
            cast.TryGetValue(Crew.Maker, out var maker);
            ShapeSeconds = PaintSeconds = 0f;
            // F49: the first make of a thing is its shape only (white clay); its colours come in the finish step,
            // when Meshy retextures this shape (ShapeTaskId) with the picked finish picture.
            bool shapeOnly = FormFirst && !finishDone;
            PreparePartNames(picture); // F83: which parts this thing has is asked while Meshy shapes it
            yield return BeginSculpt(spot, turn, widthMm);
            string path = null, texturePath = null;
            bool painted = false;
            if (Scripted && !LiveMeshyInTests)
            {
                yield return new WaitForSeconds(ScriptedShapeSeconds);
                if (Current == Step.Planning)
                {
                    string model = ScriptedModel != null && System.IO.File.Exists(ScriptedModel) ? ScriptedModel : "scripted:stand";
                    StartParts(FitShapes.Source(model)); // F83
                    TurnToMeasuredSidesNow(FitShapes.Source(model)); // F88b: its long side along the person's width
                    yield return ShapeArrived(FitShapes.Source(model), KeyWidth(FitShapes.Source(model)));
                    path = model;
                    if (!shapeOnly)
                    {
                        if (maker != null) StartCoroutine(PaintModel(maker));
                        yield return new WaitForSeconds(ScriptedPaintSeconds);
                        string png = System.IO.Path.ChangeExtension(model, ".png");
                        texturePath = model != "scripted:stand" && System.IO.File.Exists(png) ? png : null;
                        painted = true;
                    }
                }
            }
            else
            {
                MeshyClient.Job shape = null;
                for (int attempt = 0; attempt < 2 && Current == Step.Planning && (shape == null || shape.error != null); attempt++)
                {
                    shape = new MeshyClient.Job();
                    if (picture != null) StartCoroutine(MeshyClient.ImageToShape(picture, shape));
                    else StartCoroutine(MeshyClient.TextTo3D((Need + ". " + ShapeBrief() + ". " + Style).Trim(), shape));
                    for (float until = Time.time + 120f; !shape.Done && Time.time < until && Current == Step.Planning; ) yield return null;
                    if (!shape.Done) shape.error = shape.error ?? "The 3D shape took too long.";
                    if (shape.error != null) Debug.LogWarning("[Windups Shapes] shape try " + (attempt + 1) + ": " + shape.error);
                }
                if (shape != null && shape.error == null && shape.mesh != null && Current == Step.Planning)
                {
                    ShapeSeconds = shape.Seconds;
                    StartParts(shape.mesh); // F83: its parts are found (Meta's SAM) while Maker carves
                    TurnToMeasuredSidesNow(shape.mesh); // F88b: its long side along the person's width
                    yield return ShapeArrived(shape.mesh, KeyWidth(shape.mesh));
                    if (shape.id != null) ShapeTaskId = shape.id; // F49: retextured later with the finish picture
                    if (!shapeOnly && picture != null && shape.id != null)
                    {
                        if (maker != null) StartCoroutine(PaintModel(maker));
                        var paint = new MeshyClient.Job();
                        StartCoroutine(MeshyClient.Retexture(shape.id, picture, paint));
                        for (float until = Time.time + 300f; !paint.Done && Time.time < until && Current == Step.Planning; ) yield return null;
                        if (paint.Done && paint.error == null && paint.obj != null) { PaintSeconds = paint.Seconds; SaveModel(paint, out path, out texturePath); painted = texturePath != null; }
                        else Debug.LogWarning("[Windups Shapes] colours: " + (paint.error ?? "took too long"));
                    }
                    // No colours: the shape stays in clay colour (the person can paint it).
                    if (path == null && Current == Step.Planning) SaveModel(shape, out path, out texturePath);
                    if (!painted && path != null && !shapeOnly) Say(Crew.Maker, "The colours didn't come. You can paint it yourself!");
                }
                else ModelError = shape?.error ?? (Current == Step.Planning ? "The 3D model took too long." : null);
            }
            Modelling = false;
            working.Remove(Crew.Maker);
            if (Current != Step.Planning) { SculptFail(); yield break; } // the person moved on
            if (path == null)
            {
                SculptFail();
                Say(Crew.Maker, ModelError ?? "The 3D model didn't come. Say that again?");
                pendingChange = null;
                Current = Product != null ? Step.Done : Step.Shape;
                RevisionFailed();
                yield break;
            }
            ModelsMade++;
            PartsNamed(path); // F83: the parts go beside this model's file (now, or when they are found)
            var source = FitShapes.Source(path);
            var design = new CoasterDesign
            {
                kind = "model", model = path, texture = texturePath, cupDiameter = KeyWidth(source),
                name = Look != null ? MakerRoles.Clean(Look.label) : Capital(Regex.Replace(Need, @"^(a|an|the|my)\s+", "", RegexOptions.IgnoreCase).Split('.', ',')[0].Trim()),
                color = shapeOnly ? ClayHex : TextureColour(texturePath) ?? Palette.LastOrDefault(s => s.kind == "colour")?.hex ?? "#e8d2a8", why = "",
                // F88b: turned when its long side was put along the person's width; a later version keeps its thing's turn.
                sidesTurned = fromDraft != null ? fromDraft.sidesTurned : (Product != null || Versions.Count > 0) && Design != null ? Design.sidesTurned : sidesTurnedNow
            };
            if (design.name.Length > 28) design.name = design.name.Substring(0, 28).Trim();
            if (source != null) design.modelHeight = design.cupDiameter * source.bounds.size.y / Mathf.Max(1e-5f, Mathf.Max(source.bounds.size.x, source.bounds.size.z));
            if (fromDraft == null && Product == null) ApplyMeasuredSizes(design, source); // F61: every size the person gave
            ApplyDraftSizes(design, source); // F40: the sizes changed on the draft
            draftFrom = null;
            Design = design.Clamped();
            Note("design", Design.name, Design.Summary);
            // The painted model is revealed where it was made; Fixer checks it next, and the made thing takes
            // the view's place without a second build.
            yield return RevealModel(Design, painted);
            revealedBySculpt = true;
            yield return FitAndBuild();
            if (Sculpt != SculptStage.None) SculptFail(); // (not built: Fixer's check stopped, or the person moved on)
        }

        // Timings of the last make (seconds): Meshy's shape and its colours; scripted runs use the fixed ones.
        public float ShapeSeconds { get; private set; }
        public float PaintSeconds { get; private set; }
        public static float ScriptedShapeSeconds = 4f, ScriptedPaintSeconds = 8f;
        // Tests: real Meshy calls inside an otherwise scripted session (costs about 15 credits per make), or a
        // model Meshy made earlier (its .obj, with its .png beside it) replayed by the scripted session.
        public static bool LiveMeshyInTests;
        public static string ScriptedModel;

        private void SaveModel(MeshyClient.Job job, out string path, out string texturePath)
        {
            path = texturePath = null;
            try
            {
                Directory.CreateDirectory(ModelFolder);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                path = Path.Combine(ModelFolder, stamp + ".obj");
                File.WriteAllText(path, job.obj);
                if (job.texture != null)
                {
                    texturePath = Path.Combine(ModelFolder, stamp + ".png");
                    File.WriteAllBytes(texturePath, job.texture.EncodeToPNG());
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Windups Shapes] saving the model: " + exception.Message);
                if (path != null && !File.Exists(path)) path = null;
            }
        }

        private string TextureColour(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath) || !File.Exists(texturePath)) return null;
            var tex = new Texture2D(2, 2);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(texturePath))) return null;
                Color sum = Color.black;
                for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) sum += tex.GetPixelBilinear((x + .5f) / 16f, (y + .5f) / 16f);
                return "#" + ColorUtility.ToHtmlStringRGB(sum / 256f);
            }
            catch { return null; }
            finally { Destroy(tex); }
        }

        // The chosen picture with a change ("add a handle"), by the image model's edit.
        private IEnumerator EditPicture(byte[] jpeg, string change, Action<byte[]> done)
        {
            byte[] result = null;
            if (WindupsDirectCredentials.TryGetKey(out string key))
                using (var http = RevisionRequest(jpeg, change))
                {
                    http.SetRequestHeader("Authorization", "Bearer " + key);
                    http.timeout = 120;
                    yield return http.SendWebRequest();
                    if (http.result == UnityWebRequest.Result.Success)
                        try { result = Convert.FromBase64String((string)JObject.Parse(http.downloadHandler.text)["data"][0]["b64_json"]); }
                        catch (Exception exception) { Debug.LogWarning("[Windups Shapes] picture edit: " + exception.Message); }
                    else Debug.LogWarning("[Windups Shapes] picture edit failed: " + http.responseCode);
                }
            if (result != null) { if (!drawingRevision) LookJpeg = result; Note("look", "Redrawn: " + MakerRoles.Clean(change), "", result); }
            done(result);
        }

        // ------------------------------------------------------------ on the spot

        // The made thing's look: a Meshy model wears its own texture.
        private Material MadeMaterial(CoasterDesign d)
        {
            if (d == null || d.kind != "model" || string.IsNullOrEmpty(d.texture) || !File.Exists(d.texture)) return material;
            if (modelMaterials.TryGetValue(d.texture, out var m) && m != null) return m;
            var tex = new Texture2D(2, 2);
            if (!tex.LoadImage(File.ReadAllBytes(d.texture))) { Destroy(tex); return material; }
            tex.wrapMode = TextureWrapMode.Clamp;
            owned.Add(tex);
            var template = Resources.Load<Material>("MakerTextured");
            m = template != null ? new Material(template) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetTexture("_BaseMap", tex);
            m.mainTexture = tex;
            m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_EmissionMap")) { m.SetTexture("_EmissionMap", tex); m.SetColor("_EmissionColor", Color.white * .25f); }
            owned.Add(m);
            modelMaterials[d.texture] = m;
            return m;
        }

        // A clip lives on its desk edge: grabbed, it slides along the edge only, and stays where it is let go.
        // F41: only a clip that stands on a real edge is clamped to it (one saved after it was pulled off is free).
        private void AfterSolid(Transform made, CoasterDesign d)
        {
            if (made == null || d == null) return;
            SolidAfterMade(made, d); // F59: a model with cuts or sculpting is shown rebuilt
            rails.Remove(made);
            railSpan.Remove(made);
            if (d.kind != "clip") return;
            bool onEdge = NearestEdge(made.position, .02f, out _, out _, out _, out var ends);
            if (!onEdge && !HasEdge) return;
            rails[made] = (made.position, made.rotation * Vector3.forward, made.rotation);
            if (onEdge)
            {
                float margin = Mathf.Max(10f, d.width * .5f + 5f) * .001f;
                if (ends.y - ends.x > margin * 2f) railSpan[made] = new Vector2(ends.x + margin, ends.y - margin);
            }
            SetOccluder(made, true);
        }

        // F39: a usual height for tall things (the person measures or says their own).
        private float TallHint()
        {
            string w = (Need ?? "").ToLowerInvariant();
            return w.Contains("vase") || w.Contains("꽃병") ? 160f : w.Contains("lamp") || w.Contains("shade") || w.Contains("조명") ? 140f
                 : w.Contains("planter") || w.Contains("pot") || w.Contains("화분") ? 90f : w.Contains("tower") ? 180f : 100f;
        }

        private void ConstrainHeld()
        {
            if (held == null || !rails.TryGetValue(held, out var rail)) return;
            Vector3 along = rail.along.normalized;
            held.SetPositionAndRotation(rail.point + along * Mathf.Clamp(Vector3.Dot(held.position - rail.point, along), -.6f, .6f), rail.turn);
        }

        private bool OnRail(Transform thing) => thing != null && rails.ContainsKey(thing);

        // ------------------------------------------------------------ test entries

        public void SimulateEdge(Vector3 point, Vector3 outward) => SetEdge(point, outward);
        public void SimulateSlide(Transform thing, Vector3 to)
        {
            held = thing;
            heldBody = thing.GetComponent<Rigidbody>();
            thing.position = to;
            ConstrainHeld();
            var keep = held;
            LetGo();
            held = null;
            if (keep != null && OnRail(keep)) keep.GetComponent<Rigidbody>().isKinematic = true;
        }
    }
}
