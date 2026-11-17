using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // F83 (2026-11-14, the user: "what we make has parts, a magpie its beak and wings; can Meta's SAM find them so
    // each can be coloured and reshaped?"): a made model's own parts.
    //  - While Maker carves, the parts are found in the background: the AI names what this object has ("beak",
    //    "wing"; any object, nothing is fixed), PartSegmenter draws the shape from 8 sides and asks Meta's SAM
    //    for each word, and the answer is a PartField kept beside the model's OBJ. Nothing waits for it: a thing
    //    built before it arrives is split again when it does (it looks the same; only its parts change).
    //  - The parts are the brush's parts (F70: one touch floods the touched part), the 3MF's colours and the
    //    rebuilt solid's parts, through FitShapes.PartsOf / ClassifierOf. Without parts (no key, no internet, a
    //    plain one-piece thing) a model keeps top, side and bottom as before.
    //  - A fingertip near a part lights it and shows its name on a small paper tag.
    //  - "Make the beak longer / bigger / thinner" (or "this" with a fingertip on it) changes only that part
    //    (PartShape), at once and on the headset: no new Meshy run. "This is the tail" renames the touched part.
    public sealed partial class MakerSession
    {
        public static bool PartsOn = true;
        // Checks: the part words a scripted make looks for (null: a scripted make has no parts, as before F83).
        public static string[] ScriptedParts;
        public const float PartHoverReach = .025f;   // a fingertip this near a part lights it
        public const float PartHoverLinger = .3f;    // ... and for this long after it left
        public const float PartSpokenFor = 5f;       // "this" means the part touched within these seconds
        public const string PartsTaughtKey = "thinkmade.partsTaught";

        public int PartsFound { get; private set; }          // makes whose parts were found
        public int PartEditsMade { get; private set; }
        public int PartRefreshes { get; private set; }
        public string PartsReport { get; private set; } = "";
        public bool PartsBusy => partNamesPending || (partsJob != null && !partsJob.done);
        public Vector3? PartTipOverride;                     // checks: a fingertip
        public string HoverPart => HoverLive ? hoverThing.Parts[hoverPart] : null;
        private bool HoverLive => hoverThing != null && hoverPart >= 0 && hoverThing.Parts != null && hoverPart < hoverThing.Parts.Length && Time.time - hoverAt <= PartHoverLinger;

        // Places and surfaces are not parts (SAM finds nothing sure for them, or the whole thing).
        private static readonly string[] NotParts = { "body", "top", "side", "bottom", "front", "back", "inside", "outside", "edge", "rim", "surface", "hole", "opening", "middle", "center", "centre" };
        private float partsWorstFrame;

        private sealed class PartsReply { public Item[] parts; public sealed class Item { public string name, ko; } }

        private int partsMake;
        private PartSegmenter.Job partsJob;
        private PartField partsFound;
        private string partsModel;
        private bool partNamesPending;
        private string[] partWords, partSpoken;

        private PaintableThing hoverThing, touchedThing;
        private int hoverPart = -1, touchedPart = -1;
        private float hoverAt = -10f, touchedAt = -10f, hoverCheckAt;
        private Vector3 hoverPoint, hoverNormal;
        private Material hoverLook, hoverFrom;
        private Color hoverBase;
        private Transform partTag;
        private TextMeshProUGUI partTagText;
        private string partTagShown;

        private PartEdit[] partUndo;
        private Transform partUndoThing;
        private float partEditAt = -100f;

        // ------------------------------------------------------------ finding the parts while Maker carves

        // At the start of a make: what parts does this thing have? (Asked while Meshy shapes it.)
        private void PreparePartNames(byte[] picture)
        {
            partsMake++;
            if (partsJob != null) partsJob.cancelled = true;
            partsJob = null;
            partsFound = null;
            partsModel = null;
            partWords = partSpoken = null;
            partNamesPending = false;
            if (!PartsOn) return;
            EnsureSolidTicker(); // (PartsFrame runs from it, also during the first make)
            if (Scripted) { partWords = ScriptedParts; return; }
            if (!MetaSam.Available) return;
            partNamesPending = true;
            StartCoroutine(AskPartNames(partsMake, picture));
        }

        private IEnumerator AskPartNames(int make, byte[] picture)
        {
            PartsReply reply = null;
            if (WindupsDirectCredentials.TryGetKey(out string key))
            {
                var input = new { thing = Need, forObject = TargetName ?? "" };
                NoteAsk("parts", input);
                using (var http = new UnityWebRequest(WindupsDirectMeeting.Endpoint, "POST"))
                {
                    http.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(MakerRoles.CreateRequest("parts", input, picture != null ? new List<byte[]> { picture } : null)));
                    http.downloadHandler = new DownloadHandlerBuffer();
                    http.SetRequestHeader("Content-Type", "application/json");
                    http.SetRequestHeader("Authorization", "Bearer " + key);
                    http.timeout = 40;
                    yield return http.SendWebRequest();
                    if (this == null) yield break;
                    if (http.result == UnityWebRequest.Result.Success)
                        try { reply = JsonConvert.DeserializeObject<PartsReply>(MakerRoles.ReadOutput(http.downloadHandler.text)); }
                        catch (Exception exception) { Debug.LogWarning("[Windups Parts] names: " + exception.Message); }
                    else Debug.LogWarning("[Windups Parts] names: " + http.responseCode + " " + http.error);
                }
            }
            if (make != partsMake) yield break;
            // (the names the job is told never to give still come now and then: "rim" for a mug)
            var items = reply?.parts?.Where(i => i != null && !string.IsNullOrWhiteSpace(i.name) && Array.IndexOf(NotParts, MetaSam.Plain(i.name)) < 0).Take(PartSegmenter.MaxWords).ToList();
            if (items != null && items.Count > 0)
            {
                partWords = items.Select(i => i.name).ToArray();
                partSpoken = items.Select(i => (i.ko ?? "").Trim()).ToArray();
            }
            else PartsReport = "no parts named for this thing";
            partNamesPending = false;
        }

        // The shape just arrived (Meshy's mesh, or the scripted stand-in): look for the parts on it.
        private void StartParts(Mesh shape)
        {
            if (!PartsOn || shape == null) return;
            if (!partNamesPending && (partWords == null || partWords.Length == 0)) return;
            StartCoroutine(FindParts(partsMake, shape.vertices, shape.triangles));
        }

        private IEnumerator FindParts(int make, Vector3[] vertices, int[] triangles)
        {
            for (float until = Time.time + 30f; partNamesPending && Time.time < until && make == partsMake; ) yield return null;
            if (make != partsMake || partWords == null || partWords.Length == 0) yield break;
            var job = new PartSegmenter.Job { words = partWords, spoken = partSpoken };
            partsJob = job;
            partsWorstFrame = 0f;
            yield return PartSegmenter.Run(this, vertices, triangles, job);
            if (this == null || make != partsMake) yield break;
            PartsReport = job.error ?? job.report;
            // (the slowest frame while the parts were being found: on the headset this is the number to watch)
            WriteInputLog("parts | " + string.Join(", ", partWords) + " | " + PartsReport + " | worst frame " + (partsWorstFrame * 1000f).ToString("0") + " ms", true);
            if (job.error != null) Debug.LogWarning("[Windups Parts] " + job.error);
            if (job.field == null) yield break;
            partsFound = job.field;
            AttachParts();
        }

        // The model's file is known (it was saved): the parts go beside it.
        private void PartsNamed(string model)
        {
            partsModel = model;
            AttachParts();
        }

        private void AttachParts()
        {
            if (partsFound == null || string.IsNullOrEmpty(partsModel)) return;
            var field = partsFound;
            partsFound = null;
            string key = PartField.KeyFor(partsModel, null);
            StoreParts(key, field);
            PartsFound++;
            Talk.Add("Its parts were found: " + string.Join(", ", field.names.Skip(1)) + ". The person can paint each part and ask to change a part's shape (longer, bigger, thinner).");
            // A thing of this model already stands: split it again (same look, its own parts now).
            foreach (var pair in madeDesigns.ToList())
                if (pair.Key != null && pair.Value != null && pair.Value.kind == "model" && PartField.KeyFor(pair.Value.model, pair.Value.parts) == key)
                    RefreshParts(pair.Key, pair.Value, false);
            TeachParts(field);
        }

        private void StoreParts(string key, PartField field)
        {
            PartField.Set(key, field);
            // (a scripted run's models are stand-ins or the Editor's saved ones: nothing is written beside them)
            if (Scripted || !key.EndsWith(".parts", StringComparison.Ordinal)) return;
            var bytes = field.ToBytes();
            Task.Run(() => { try { File.WriteAllBytes(key, bytes); } catch (Exception exception) { Debug.LogWarning("[Windups Parts] saving: " + exception.Message); } });
        }

        // A new look of the same shape (Meshy's retexture writes a new OBJ): it keeps the shape's parts.
        private static void KeepParts(CoasterDesign next, CoasterDesign from)
        {
            if (next == null || from == null || from.kind != "model") return;
            string key = PartField.KeyFor(from.model, from.parts);
            if (PartField.Get(key) != null) next.parts = key;
        }

        // Once ever: how the parts are used (the hover tag shows their names from then on).
        private void TeachParts(PartField field)
        {
            if (Scripted || PlayerPrefs.GetInt(PartsTaughtKey, 0) == 1 || field.names.Length < 2) return;
            PlayerPrefs.SetInt(PartsTaughtKey, 1);
            string part = PartField.BaseName(field.names[1]);
            Say(Crew.Maker, $"Touch a part to see its name. Say \"make the {part} longer\" to change it.");
        }

        // ------------------------------------------------------------ a made thing split by its parts again

        // `reshaped`: the part edits changed (its collider follows); `renamed`: a part's old and new name.
        private void RefreshParts(Transform made, CoasterDesign d, bool reshaped, (string from, string to)? renamed = null)
        {
            if (made == null || d == null) return;
            var paint = made.GetComponentInChildren<PaintableThing>();
            if (paint == null) return;
            PartRefreshes++;
            if (hoverThing == paint) hoverPart = -1;
            if (touchedThing == paint) touchedPart = -1;
            var marks = paint.SavedMarks();
            if (renamed.HasValue) foreach (var mark in marks) if (mark.part == renamed.Value.from) mark.part = renamed.Value.to;
            // F59: a thing with cuts or sculpting is shown as its rebuilt solid, which is split in SwapView.
            if (d.ops != null && d.ops.Length > 0)
            {
                if (renamed.HasValue) { foreach (var key in paint.Marks.Keys.ToList()) paint.Clear(key); ApplyMarksLater(made, marks); }
                StartCoroutine(ShowRebuilt(made, d.Copy(), null));
                return;
            }
            StartCoroutine(SplitAgain(made, d, marks, reshaped));
        }

        private readonly Dictionary<Transform, int> partSplits = new Dictionary<Transform, int>();
        public float PartSplitMs { get; private set; }   // the main thread's share of the last split (tests)

        // The model's points, normals and parts are worked out off the main thread (FPS); the main thread only
        // fetches the model's arrays and hands the finished ones to the mesh.
        private int splitsRunning;
        private readonly Dictionary<Transform, Mesh> splitMeshes = new Dictionary<Transform, Mesh>();

        private IEnumerator SplitAgain(Transform made, CoasterDesign d, List<PaintableThing.Mark> marks, bool reshaped)
        {
            splitsRunning++;
            try { yield return Split(made, d, marks, reshaped); }
            finally { splitsRunning--; }
        }

        private IEnumerator Split(Transform made, CoasterDesign d, List<PaintableThing.Mark> marks, bool reshaped)
        {
            int turn = partSplits[made] = (partSplits.TryGetValue(made, out int before) ? before : 0) + 1;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var input = FitShapes.ModelSource(d);
            var paint = made.GetComponentInChildren<PaintableThing>();
            if (input == null || input.parts == null || paint == null) yield break;
            var filter = paint.GetComponent<MeshFilter>();
            var renderer = paint.GetComponent<MeshRenderer>();
            float metres = Mathf.Abs(filter.transform.lossyScale.x) / PaintableThing.TileSize;
            Color tint = d.Tint(new Color(.95f, .85f, .62f));
            string name = "Model " + d.name;
            float mainMs = (float)watch.Elapsed.TotalMilliseconds;
            var work = Task.Run(() =>
            {
                var v = FitShapes.ModelCompute(input);
                var normals = MeshSmooth.VertexNormals(v, input.triangles);
                var built = PartField.Over(input.parts, v, input.triangles, input.labels);
                var colours = new Color[v.Length];
                for (int i = 0; i < colours.Length; i++) colours[i] = tint;
                var bounds = new Bounds(v[0], Vector3.zero);
                foreach (var point in v) bounds.Encapsulate(point);
                var prepared = PaintableThing.Prepare(v, normals, colours, input.uv, input.triangles, bounds, metres, built.Classifier(), built.names, name);
                return (v, normals, colours, built, prepared);
            });
            while (!work.IsCompleted) yield return null;
            if (work.IsFaulted) { Debug.LogWarning("[Windups Parts] split: " + work.Exception?.GetBaseException().Message); yield break; }
            if (made == null || filter == null || partSplits[made] != turn) yield break; // gone, or split again meanwhile
            watch.Restart();
            var done = work.Result;
            FitShapes.RememberLabels(input);
            FitShapes.RememberBuilt(input, done.built);
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.vertices = done.v;
            if (input.uv != null && input.uv.Length == done.v.Length) mesh.uv = input.uv;
            mesh.triangles = input.triangles;
            mesh.normals = done.normals;
            mesh.colors = done.colours;
            mesh.RecalculateBounds();
            owned.Add(mesh);
            // (the knob's live view splits many times: the last split's mesh goes)
            if (splitMeshes.TryGetValue(made, out var last) && last != null && last != mesh) { owned.Remove(last); Destroy(last); }
            splitMeshes[made] = mesh;
            renderer.sharedMaterials = new[] { MadeMaterial(d) };
            PaintableThing.MakePrepared(filter, renderer, done.prepared, false);
            ApplyMarks(made, marks);
            if (made == Product) ProductMesh = mesh;
            PartSplitMs = mainMs + (float)watch.Elapsed.TotalMilliseconds;
            if (reshaped) yield return BakeCollider(made, mesh);
        }

        private void ApplyMarksLater(Transform made, List<PaintableThing.Mark> marks) => StartCoroutine(MarksAfterRebuild(made, marks));

        private IEnumerator MarksAfterRebuild(Transform made, List<PaintableThing.Mark> marks)
        {
            int views = SolidViews;
            for (float until = Time.time + 20f; SolidViews == views && Time.time < until; ) yield return null;
            if (made != null) ApplyMarks(made, marks);
        }

        // ------------------------------------------------------------ every frame: the part under a fingertip

        private void PartsFrame()
        {
            if (partsJob != null && !partsJob.done) partsWorstFrame = Mathf.Max(partsWorstFrame, Time.unscaledDeltaTime);
            IdlePartTagsFrame(); // 2026-11-15: every part's name while no hand is near (MakerSession.PartTags.cs)
            // F84: while the person sculpts, the fingertips are on the surface all the time: no lit part, no tag.
            // (2026-11-15: in Sculpt it's mode the parts are what is shaped: outlined, with their handles.)
            if (sculptMode && sculpt == null && (Product == null || Current != Step.Done)) EndSculptMode(); // (paused when the step moved on)
            if (Sculpting && !KnobOnlyWhenSculpting)
            {
                hoverPart = -1;
                if (partTag != null && partTag.gameObject.activeSelf) partTag.gameObject.SetActive(false);
                if (partKnobHand != null) EndPartKnob(false);
                HidePartKnob();
                return;
            }
            if (partKnobHand != null) { PartKnobDrag(); DrawPartHover(); DrawPartKnob(); return; } // (the part stays lit while its knob is pulled)
            if (Time.time >= hoverCheckAt)
            {
                hoverCheckAt = Time.time + .05f;
                FindPartHover();
            }
            DrawPartHover();
            DrawPartKnob();
        }

        private bool HasOwnParts(PaintableThing thing)
        {
            var root = MadeRootOf(thing.transform);
            return root != null && madeDesigns.TryGetValue(root, out var d) && FitShapes.BaseParts(d) != null;
        }

        private void FindPartHover()
        {
            PaintableThing best = null;
            int bestPart = -1;
            float bestDistance = PartHoverReach;
            Vector3 bestAt = default, bestNormal = Vector3.up;
            void Try(Vector3 tip)
            {
                foreach (var thing in PaintableThing.All)
                {
                    if (thing == null || thing.Renderer == null || !thing.Renderer.enabled || thing.Renderer.forceRenderingOff) continue; // (F94: not the one hidden under the see-through one)
                    var bounds = thing.Renderer.bounds;
                    bounds.Expand(PartHoverReach * 2f);
                    if (!bounds.Contains(tip) || !HasOwnParts(thing)) continue;
                    if (held != null && MadeRootOf(thing.transform) == held) continue; // (2026-11-15: nothing lights on a thing in the hand)
                    int part = thing.Closest(tip, out Vector3 surface, out Vector3 normal, out float distance);
                    if (part < 0 || distance > bestDistance) continue;
                    best = thing; bestPart = part; bestDistance = distance; bestAt = surface; bestNormal = normal;
                }
            }
            if (PartTipOverride.HasValue) Try(PartTipOverride.Value);
            else if (PaintableThing.All.Count > 0)
                foreach (var hand in HandSet.Both())
                {
                    if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) continue;
                    if (HeldBy(hand) != null || HandIO.Pinching(hand)) continue; // a hand carrying something points at nothing
                    if (HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip)) Try(tip.position);
                }
            if (best == null) return;
            hoverThing = touchedThing = best;
            hoverPart = touchedPart = bestPart;
            hoverAt = touchedAt = Time.time;
            hoverPoint = bestAt;
            hoverNormal = bestNormal;
        }

        // The outline: the part's own triangles drawn again, pushed out along their normals by _Width (metres) and seen
        // from inside (Windups/Part Outline, Resources/MakerPartOutline): only a rim shows round the part.
        public const float PartOutlineWidth = .0016f;
        private static readonly Color Parts90Coral = new Color(1f, .45f, .36f, 1f);
        private Material partOutline;
        private bool partOutlineLooked;
        private MaterialPropertyBlock outlineBlock;
        public int OutlineDraws { get; private set; }
        private Material PartOutline
        {
            get
            {
                if (partOutline != null || partOutlineLooked) return partOutline;
                partOutlineLooked = true;
                partOutline = Resources.Load<Material>("MakerPartOutline");
                if (partOutline == null) { var shader = Shader.Find("Windups/Part Outline"); if (shader != null) { partOutline = new Material(shader) { name = "Part Outline" }; owned.Add(partOutline); } }
                return partOutline;
            }
        }

        // The touched part a little brighter (its own look drawn once more, lit from inside), and its name.
        private void DrawPartHover()
        {
            if (!HoverLive)
            {
                if (partTag != null && partTag.gameObject.activeSelf) partTag.gameObject.SetActive(false);
                return;
            }
            var mesh = hoverThing.Mesh;
            var renderer = hoverThing.Renderer;
            if (mesh == null || renderer == null || hoverPart >= mesh.subMeshCount || !renderer.enabled || !renderer.gameObject.activeInHierarchy) return;
            var current = hoverThing.Current(hoverPart);
            // 2026-11-15 (the user's pick A of Docs/Design/Concepts/Windups_PartHighlight_2026-11-15): a coral line round
            // the part instead of the white glow, so the part keeps its own colour (also while painting).
            if (PartOutline != null)
            {
                outlineBlock ??= new MaterialPropertyBlock();
                float width = PartOutlineWidth * (1f + .12f * Mathf.Sin(Time.time * 6f));
                outlineBlock.SetFloat("_Width", width);
                outlineBlock.SetColor("_BaseColor", Parts90Coral);
                Graphics.DrawMesh(mesh, hoverThing.transform.localToWorldMatrix, PartOutline, hoverThing.gameObject.layer, null, hoverPart, outlineBlock, ShadowCastingMode.Off, false);
                OutlineDraws++;
            }
            else if (current != null && FillOn(hoverThing, hoverPart) == null)
            {
                if (hoverLook == null || hoverFrom != current)
                {
                    if (hoverLook != null) Destroy(hoverLook);
                    hoverLook = new Material(current) { name = "Part hover" };
                    hoverFrom = current;
                    hoverLook.renderQueue = current.renderQueue + 1;
                    if (hoverLook.HasProperty("_DepthOffset")) hoverLook.SetFloat("_DepthOffset", -1f);
                    hoverBase = hoverLook.HasProperty("_Glow") ? hoverLook.GetColor("_Glow") : hoverLook.HasProperty("_EmissionColor") ? hoverLook.GetColor("_EmissionColor") : Color.black;
                }
                float pulse = .2f + .07f * Mathf.Sin(Time.time * 7f);
                var glow = hoverBase + new Color(pulse, pulse, pulse * .85f, 0f);
                if (hoverLook.HasProperty("_Glow")) hoverLook.SetColor("_Glow", glow);
                else if (hoverLook.HasProperty("_EmissionColor")) { hoverLook.EnableKeyword("_EMISSION"); hoverLook.SetColor("_EmissionColor", glow); }
                Graphics.DrawMesh(mesh, hoverThing.transform.localToWorldMatrix, hoverLook, hoverThing.gameObject.layer, null, hoverPart, null, ShadowCastingMode.Off, false);
            }
            // The name, on paper, just off the surface toward the person.
            string name = Capital(PartField.BaseName(hoverThing.Parts[hoverPart]));
            if (partTag == null)
            {
                partTag = MakePartTag("Part Tag", out partTagText); // F102: coral, bigger
            }
            if (partTagShown != name) { partTagShown = name; if (partTagText != null) partTagText.text = name; }
            // (above the part, so the tag never covers what it names; F102: in front of whatever of the thing is there)
            Vector3 toHead = Head != null ? (Head.position - hoverPoint).normalized : hoverNormal;
            Vector3 above = hoverPoint + Vector3.up * .04f;
            Vector3 at = Head != null ? FrontOf(hoverThing, hoverThing.Renderer.bounds, above, Head.position) : above;
            at += (Head != null ? (Head.position - at).normalized : toHead) * PartTagOut;
            partTag.position = at;
            partTag.rotation = SpeechBubbleDock.FacingAt(at);
            if (!partTag.gameObject.activeSelf) partTag.gameObject.SetActive(true);
        }

        // ------------------------------------------------------------ words: a part's shape, a part's name

        private static readonly (string[] words, string kind, float factor, string said)[] PartChanges =
        {
            (new[] { "longer", "taller", "길게", "길어", "길쭉" }, "length", 1.3f, "longer"),
            (new[] { "shorter", "짧게", "짧아" }, "length", .77f, "shorter"),
            (new[] { "bigger", "larger", "크게", "커지", "키워" }, "size", 1.25f, "bigger"),
            (new[] { "smaller", "tinier", "작게", "작아", "줄여" }, "size", .8f, "smaller"),
            (new[] { "thicker", "fatter", "wider", "chunkier", "두껍게", "굵게", "넓게", "통통" }, "width", 1.3f, "thicker"),
            (new[] { "thinner", "slimmer", "narrower", "skinnier", "얇게", "가늘게", "좁게" }, "width", .77f, "thinner"),
        };
        private static readonly string[] NotPartNames =
        {
            "good", "great", "nice", "cool", "perfect", "fine", "wrong", "bad", "right", "ok", "okay", "done", "it", "mine", "cute", "pretty", "ugly", "big", "small",
            "long", "short", "fun", "weird", "awesome", "beautiful", "amazing", "better", "all", "enough", "what", "one", "top", "side", "bottom", "body"
        };

        private static bool Says(string p, string word) =>
            word.Length > 0 && (word[0] < 128 ? Regex.IsMatch(p, @"\b" + Regex.Escape(word) + @"\b") : p.Contains(word));

        // Called early in OnVoice / Route: only takes words about a part of the made model.
        private bool PartVoice(string text, string p)
        {
            if (Product == null || Design == null || Design.kind != "model" || Modelling || Sculpt != SculptStage.None || (Sculpting && !KnobOnlyWhenSculpting)) return false; // (F84: sculpting has its own words; 2026-11-15: in Sculpt it's mode parts are named too)
            var field = FitShapes.BaseParts(Design);
            if (field == null) return false;
            bool touching = touchedThing != null && touchedPart >= 0 && Time.time - touchedAt <= PartSpokenFor && touchedThing.Parts != null && touchedPart < touchedThing.Parts.Length
                && MadeRootOf(touchedThing.transform) == Product;
            int touched = touching ? Array.IndexOf(field.names, touchedThing.Parts[touchedPart]) : -1;

            // "undo" right after a part was changed.
            if (partUndoThing == Product && Time.time - partEditAt < 90f && partEditAt > brushUsedAt && partEditAt > sculptStrokeAt && Regex.IsMatch(p, @"^(undo|undo that|put it back|go back|되돌려|취소)\b"))
            {
                SetPartEdits(partUndo, null, "undone");
                partUndoThing = null;
                Ack(Crew.Maker, "Back as it was.");
                KeepSculptQuestion(SculptUndoneLine);
                return true;
            }

            // "this is the tail": the touched part's name.
            var naming = Regex.Match(p, @"^(?:no |and |so )?(?:this|that|it)(?:'s| is)(?: called)? (?:the |a |an |its |my )?([a-z]{2,14})$");
            if (naming.Success && touched > 0 && Array.IndexOf(NotPartNames, naming.Groups[1].Value) < 0)
            {
                RenamePart(field, touched, naming.Groups[1].Value);
                return true;
            }

            // "make the beak longer", "bigger wings", "this a bit thinner".
            int change = -1;
            for (int i = 0; i < PartChanges.Length && change < 0; i++) if (PartChanges[i].words.Any(w => Says(p, w))) change = i;
            if (change < 0) return false;
            var labels = new List<int>();
            for (int i = 1; i < field.names.Length; i++)
            {
                string name = PartField.BaseName(field.names[i]);
                bool one = Says(p, name) || (!string.IsNullOrEmpty(field.spoken[i]) && p.Contains(field.spoken[i]));
                bool many = Says(p, name + "s") || Says(p, name + "es");
                if (!one && !many) continue;
                // "the wing" with a fingertip on one wing is that wing; "the wings", or nothing touched, is every wing.
                if (one && !many && touched > 0 && PartField.BaseName(field.names[touched]) == name) { if (!labels.Contains(touched)) labels.Add(touched); }
                else foreach (int l in field.LabelsNamed(name)) if (!labels.Contains(l)) labels.Add(l);
            }
            if (labels.Count == 0 && touched > 0 && (Regex.IsMatch(p, @"\b(this|that|it|here)\b") || p.Contains("이거") || p.Contains("이것") || p.Contains("여기") || p.Contains("이 부분") || p.Contains("저거")))
                labels.Add(touched);
            if (labels.Count == 0) return false; // not about a part: the other routes take it (v2, sizes)
            float factor = PartChanges[change].factor;
            if (Regex.IsMatch(p, @"\b(a bit|a little|slightly|a touch)\b") || p.Contains("조금") || p.Contains("살짝") || p.Contains("약간")) factor = 1f + (factor - 1f) * .5f;
            else if (Regex.IsMatch(p, @"\b(much|a lot|way|lots|really)\b") || p.Contains("훨씬") || p.Contains("많이") || p.Contains("엄청")) factor = 1f + (factor - 1f) * 1.8f;
            ChangeParts(field, labels, PartChanges[change].kind, factor, PartChanges[change].said);
            return true;
        }

        private void ChangeParts(PartField field, List<int> labels, string kind, float factor, string said)
        {
            var edits = (Design.partEdits ?? new PartEdit[0]).Where(e => e != null).Select(e => e.Copy()).ToList();
            foreach (int label in labels)
            {
                string name = field.names[label];
                var edit = edits.FirstOrDefault(e => e.part == name && e.kind == kind);
                if (edit == null) edits.Add(new PartEdit { part = name, kind = kind, factor = factor });
                else edit.factor = Mathf.Clamp(edit.factor * factor, .4f, 2.6f);
            }
            edits.RemoveAll(e => Mathf.Abs(e.factor - 1f) < .01f);
            string part = PartField.BaseName(field.names[labels[0]]);
            SetPartEdits(edits.ToArray(), Design.partEdits, part + " " + said);
            PartEditsMade++;
            Ack(Crew.Maker, Capital(said) + " " + part + (labels.Count > 1 ? "s" : "") + "!");
            KeepSculptQuestion(SculptLessonDoneLine);
        }

        // In Sculpt it's mode, words closed the Done | Undo question: it comes back (not over a lesson).
        private void KeepSculptQuestion(string line)
        {
            if (SculptMode && CurrentLesson == null && (Question == null || Question.answered)) AskSculptDone(line);
        }

        // The made thing takes the edits where it stands (its paint stays), and they are saved with it.
        private void SetPartEdits(PartEdit[] edits, PartEdit[] before, string what)
        {
            // 2026-11-15: in Sculpt it's mode the clay's touches go into the design first (they are carried along with
            // the surface), and the clay comes back on the new shape.
            bool resume = sculptMode && KnobOnlyWhenSculpting;
            if (resume) PauseSculpt();
            partUndo = before;
            partUndoThing = before != null || what != "undone" ? Product : null;
            partEditAt = Time.time;
            var was = Design.Copy();
            Design.partEdits = edits != null && edits.Length > 0 ? edits : null;
            Heard();
            // F59 cuts and sculpting dabs sit at places on the surface: they go with it (worked out off the main thread).
            if (Design.ops != null && Design.ops.Any(o => o?.at != null)) StartCoroutine(CarryOpsThenApply(was, what));
            else ApplyPartEdits(what);
            if (resume) StartCoroutine(ResumeSculpt(true));
        }

        private IEnumerator CarryOpsThenApply(CoasterDesign was, string what)
        {
            var product = Product;
            var design = Design;
            var from = FitShapes.ModelSource(was);
            var to = FitShapes.ModelSource(design);
            var ops = design.ops;
            var carry = Task.Run(() => FitShapes.CarryOps(from, to, ops));
            while (!carry.IsCompleted) yield return null;
            if (Product != product || Design != design || Product == null) yield break; // made again meanwhile
            if (!carry.IsFaulted && carry.Result != null) { Design.ops = carry.Result; OpsCarried++; }
            else if (carry.IsFaulted) Debug.LogWarning("[Windups Parts] carrying the cuts: " + carry.Exception?.GetBaseException().Message);
            ApplyPartEdits(what);
        }

        public int OpsCarried { get; private set; }   // part edits that moved cuts or dabs along (tests)

        private void ApplyPartEdits(string what)
        {
            madeDesigns[Product] = Design;
            var version = Versions.FirstOrDefault(v => v.root == Product);
            if (version != null) version.design = Design.Copy();
            if (endingLastProduct == Product) endingLastDesign = Design.Copy();
            RefreshParts(Product, Design, true);
            var paint = Product.GetComponentInChildren<PaintableThing>();
            Vector3 at = paint != null && paint.Renderer != null ? paint.Renderer.bounds.center : Product.position;
            ToyFx.Puff(at, .012f, 4);
            ToySounds.Play(ToySounds.Cue.Pop, at, .5f, 1.25f);
            Talk.Add("Maker changed a part of the made thing: " + what);
            WriteInputLog("part edit | " + what + " | " + (Design.partEdits == null ? "none" : string.Join(", ", Design.partEdits.Select(e => e.part + " " + e.kind + " x" + e.factor.ToString("0.00")))), true);
            Remember();
        }

        private void RenamePart(PartField field, int label, string word)
        {
            string old = field.names[label];
            string name = word;
            for (int n = 2; Array.IndexOf(field.names, name) >= 0 && n < 9; n++) name = word + " " + n;
            if (name == old) return;
            string key = PartField.KeyFor(Design.model, Design.parts);
            StoreParts(key, field.Renamed(label, name, ""));
            if (Design.partEdits != null)
                Design.partEdits = Design.partEdits.Select(e => { var c = e.Copy(); if (c.part == old) c.part = name; return c; }).ToArray();
            foreach (var pair in madeDesigns.ToList())
                if (pair.Key != null && pair.Value != null && pair.Value.kind == "model" && PartField.KeyFor(pair.Value.model, pair.Value.parts) == key)
                    RefreshParts(pair.Key, pair.Value, false, (old, name));
            Heard();
            ToySounds.Play(ToySounds.Cue.Pop, hoverPoint, .4f, 1.5f);
            Talk.Add("The person named a part of the made thing: the " + name + " (it was called " + old + ")");
            WriteInputLog("part name | " + old + " -> " + name, true);
            Ack(Crew.Maker, "The " + name + ". Got it!");
            Remember();
        }

        // ------------------------------------------------------------ the knob on a part's tip (the user's pick A, 2026-11-15)

        // A fingertip near a part lights it and puts a small coral knob on a short stem just past its tip (where it
        // sticks out the farthest from where it joins the rest). Pinched near (it is in NearestGrab, so the pinch takes
        // the knob and not the whole thing) and pulled along the part, the part grows longer or shorter while the hand
        // moves; let go, the change is kept as a part edit (undo, saved, printed) like "make the beak longer". Near
        // only, never from afar: the knob is small and right on the thing (F66). Plain pinch-and-pull, not grab &
        // release: the fingers opening ends it. The rest ("body") has no knob.
        public const float KnobRadius = .0055f;        // the knob's size
        public const float KnobOut = .009f;            // how far past the tip it sits
        public const float KnobGrab = .025f;           // a pinch this near takes it
        public const float KnobLinger = 1.2f;          // it stays this long after the fingertip left the part (to reach it)
        public const float KnobStep = .05f;            // the length goes in steps of 5%
        public static readonly Color KnobColour = new Color(1f, .5f, .42f);

        public int KnobPulls { get; private set; }     // pulls that changed a part (tests)
        public Vector3? PartKnobPosition => partKnob != null && partKnob.gameObject.activeSelf ? partKnob.position : (Vector3?)null;
        public string PartKnobPart => knobPart;
        public bool PartKnobHeld => partKnobHand != null;

        private Transform partKnob;
        private Hand partKnobHand;
        private PaintableThing knobThing;
        private string knobPart;
        private int knobSub = -1;
        private float knobSeenAt = -10f, knobPreviewFactor = 1f, knobStartFactor = 1f, knobReachWorld, knobTickFactor = 1f;
        private Vector3 knobAnchorWorld, knobAxisWorld, knobAt;
        private PartEdit[] knobStartEdits;
        private Mesh knobMeasuredMesh;
        private bool knobMeasuring, knobHasFrame;
        private Vector3 knobAnchorLocal, knobAxisLocal;
        private float knobReachLocal;

        // 2026-11-15 (second Quest try: a coral circle stuck to the hand holding the horse, and a leg got 1.3x longer by
        // accident): the handle shows only in Sculpt it's mode (MakerSession.SculptHands.cs), where the thing stands
        // still; KnobOnlyWhenSculpting = false keeps the F83 handle outside it (older checks).
        public static bool KnobOnlyWhenSculpting = true;
        private bool KnobAllowed =>
            Product != null && Design != null && Design.kind == "model" && !Modelling && Sculpt == SculptStage.None && !Drawing &&
            (KnobOnlyWhenSculpting ? SculptMode : !Sculpting);

        // The knob for the part that is lit now (where it goes is worked out once per part and mesh, in a Task).
        private void DrawPartKnob()
        {
            if (!KnobAllowed) { HidePartKnob(); return; }
            if (partKnobHand == null)
            {
                bool lit = HoverLive && PartField.BaseName(hoverThing.Parts[hoverPart]) != PartField.Rest && MadeRootOf(hoverThing.transform) == Product;
                if (lit && (knobThing != hoverThing || knobSub != hoverPart || knobMeasuredMesh != hoverThing.Mesh))
                {
                    if (knobThing != hoverThing || knobSub != hoverPart) knobHasFrame = false;
                    knobThing = hoverThing;
                    knobSub = hoverPart;
                    knobPart = hoverThing.Parts[hoverPart];
                    MeasureKnob(hoverThing, hoverPart);
                }
                if (lit) knobSeenAt = Time.time;
                if (knobThing == null || !knobHasFrame || Time.time - knobSeenAt > KnobLinger) { HidePartKnob(); return; }
                var t = knobThing.transform;
                Vector3 axis = t.TransformDirection(knobAxisLocal).normalized;
                knobAt = t.TransformPoint(knobAnchorLocal + knobAxisLocal * knobReachLocal) + axis * KnobOut;
            }
            if (partKnob == null)
            {
                partKnob = ColourBall("Part Knob", KnobColour, KnobRadius);
                // (a thin stem from the tip to the knob: it reads as a handle on the part, not a loose bead)
                var stem = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(stem.GetComponent<Collider>());
                stem.name = "Stem";
                stem.transform.SetParent(partKnob, false);
                var r = stem.GetComponent<MeshRenderer>();
                r.sharedMaterial = partKnob.GetComponent<MeshRenderer>().sharedMaterial;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                var block = new MaterialPropertyBlock();
                block.SetColor("_BaseColor", Color.Lerp(KnobColour, Color.white, .35f));
                block.SetFloat("_VertexColor", 0f);
                r.SetPropertyBlock(block);
            }
            // The stem points back to the tip; the knob swells when a fingertip comes near (it can be pinched).
            Vector3 back = partKnobHand != null ? -knobAxisWorld : -(knobThing.transform.TransformDirection(knobAxisLocal).normalized);
            float near = float.MaxValue;
            foreach (var hand in HandSet.Both())
                if (hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand)) near = Mathf.Min(near, Vector3.Distance(PinchPoint(hand), knobAt));
            float grow = partKnobHand != null ? 1.35f : near < KnobGrab * 1.6f ? 1.2f + .06f * Mathf.Sin(Time.time * 9f) : 1f;
            float size = KnobRadius * 2f * grow;
            partKnob.position = knobAt;
            partKnob.rotation = Quaternion.FromToRotation(Vector3.up, back);
            partKnob.localScale = Vector3.one * size;
            var stemView = partKnob.Find("Stem");
            if (stemView != null)
            {
                // A Unity cylinder is 2 units tall: the stem runs from the ball's middle to the part's tip.
                float length = KnobOut / size;
                stemView.localScale = new Vector3(.22f, length * .5f, .22f);
                stemView.localPosition = Vector3.up * (length * .5f);
            }
            if (!partKnob.gameObject.activeSelf) partKnob.gameObject.SetActive(true);
        }

        private void HidePartKnob()
        {
            if (partKnob != null && partKnob.gameObject.activeSelf) partKnob.gameObject.SetActive(false);
        }

        private void MeasureKnob(PaintableThing thing, int sub)
        {
            var mesh = thing.Mesh;
            if (mesh == null || knobMeasuring) return;
            knobMeasuring = true;
            var v = mesh.vertices;
            int count = mesh.subMeshCount;
            var parts = new int[count][];
            for (int k = 0; k < count; k++) parts[k] = mesh.GetTriangles(k);
            StartCoroutine(MeasureKnobLater(thing, sub, mesh, v, parts));
        }

        private IEnumerator MeasureKnobLater(PaintableThing thing, int sub, Mesh mesh, Vector3[] v, int[][] parts)
        {
            var work = Task.Run(() =>
            {
                int total = 0;
                foreach (var p in parts) total += p.Length;
                var tri = new int[total];
                var labels = new int[total / 3];
                int at = 0;
                for (int k = 0; k < parts.Length; k++)
                {
                    Array.Copy(parts[k], 0, tri, at, parts[k].Length);
                    for (int i = 0; i < parts[k].Length / 3; i++) labels[at / 3 + i] = k;
                    at += parts[k].Length;
                }
                bool ok = PartShape.Frame(v, tri, labels, sub, out var anchor, out var u, out float reach);
                return (ok, anchor, u, reach);
            });
            while (!work.IsCompleted) yield return null;
            knobMeasuring = false;
            if (work.IsFaulted || !work.Result.ok || thing == null || thing != knobThing || sub != knobSub) yield break;
            knobAnchorLocal = work.Result.anchor;
            knobAxisLocal = work.Result.u;
            knobReachLocal = work.Result.reach;
            knobMeasuredMesh = mesh;
            knobHasFrame = knobReachLocal > 1e-5f;
        }

        // From NearestGrab: how near a pinch at `point` is to the knob (float.MaxValue when it is not there or too far).
        private float PartKnobDistance(Vector3 point)
        {
            if (partKnobHand != null || !KnobAllowed || PartKnobPosition == null) return float.MaxValue;
            float d = Vector3.Distance(point, partKnob.position);
            return d <= KnobGrab ? d : float.MaxValue;
        }

        // From NearestPinch: the knob is taken.
        private bool PartKnobPinch(Hand hand, Vector3 point)
        {
            if (PartKnobDistance(point) == float.MaxValue || knobThing == null) return false;
            var field = FitShapes.BaseParts(Design);
            if (field == null || Array.IndexOf(field.names, knobPart) <= 0) return false;
            if (sculptMode && KnobOnlyWhenSculpting) PauseSculpt(); // 2026-11-15: the clay's touches go into the design while the part is pulled
            partKnobHand = hand;
            knobStartEdits = Design.partEdits;
            var edit = Design.partEdits?.FirstOrDefault(e => e != null && e.part == knobPart && e.kind == "length");
            knobStartFactor = knobPreviewFactor = knobTickFactor = edit != null ? edit.factor : 1f;
            var t = knobThing.transform;
            knobAxisWorld = t.TransformDirection(knobAxisLocal).normalized;
            knobAnchorWorld = t.TransformPoint(knobAnchorLocal);
            knobReachWorld = Mathf.Max(.002f, t.TransformVector(knobAxisLocal * knobReachLocal).magnitude);
            Heard();
            ToySounds.Play(ToySounds.Cue.Press, knobAt, .35f, 1.7f);
            WriteInputLog("part knob | " + knobPart + " taken", true);
            return true;
        }

        // The part follows the hand along its own line (only its length changes), in steps of 5%.
        private void PartKnobDrag()
        {
            var hand = partKnobHand;
            if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !KnobAllowed || !HandIO.Pinching(hand)) { EndPartKnob(true); return; }
            // (the part stays lit while it is pulled, also across the live view's rebuilds)
            if (knobThing != null) { hoverThing = knobThing; hoverPart = knobSub; hoverAt = Time.time; }
            Vector3 point = PinchPoint(hand);
            float along = Vector3.Dot(point - knobAnchorWorld, knobAxisWorld) - KnobOut;
            float factor = knobStartFactor * Mathf.Max(.05f, along) / knobReachWorld;
            factor = Mathf.Clamp(Mathf.Round(factor / KnobStep) * KnobStep, .4f, 2.6f);
            // The knob rides on the part's line at the length it gives (it never leaves the line).
            knobAt = knobAnchorWorld + knobAxisWorld * (knobReachWorld * factor / knobStartFactor + KnobOut);
            if (Mathf.Abs(factor - knobPreviewFactor) < .001f) return;
            knobPreviewFactor = factor;
            if (Mathf.Abs(factor - knobTickFactor) >= .099f) { knobTickFactor = factor; ToySounds.Play(ToySounds.Cue.Press, knobAt, .08f, 2.3f); }
            // A live view while pulling, one rebuild at a time (a thing with cuts or sculpting shows it on let go).
            Design.partEdits = KnobEdits(factor);
            if (splitsRunning == 0 && (Design.ops == null || Design.ops.Length == 0)) RefreshParts(Product, Design, false);
        }

        private PartEdit[] KnobEdits(float factor)
        {
            var edits = (knobStartEdits ?? new PartEdit[0]).Where(e => e != null).Select(e => e.Copy()).ToList();
            var edit = edits.FirstOrDefault(e => e.part == knobPart && e.kind == "length");
            if (edit == null) edits.Add(edit = new PartEdit { part = knobPart, kind = "length", factor = factor });
            edit.factor = factor;
            edits.RemoveAll(e => Mathf.Abs(e.factor - 1f) < .01f);
            return edits.Count > 0 ? edits.ToArray() : null;
        }

        // Let go: the length is kept as a part edit (undo, saved), or nothing changed.
        private void EndPartKnob(bool keep)
        {
            if (partKnobHand == null) return;
            partKnobHand = null;
            float factor = knobPreviewFactor;
            Design.partEdits = knobStartEdits;
            knobSeenAt = Time.time;
            bool changed = Mathf.Abs(factor - knobStartFactor) >= .001f;
            if (!keep || !changed || Product == null)
            {
                if (Product != null && changed) RefreshParts(Product, Design, false); // (the live view goes back)
                WriteInputLog("part knob | " + knobPart + " let go, unchanged", true);
                if (sculptPaused) StartCoroutine(ResumeSculpt(changed));
                return;
            }
            KnobPulls++;
            PartEditsMade++;
            string part = PartField.BaseName(knobPart);
            SetPartEdits(KnobEdits(factor), knobStartEdits, part + (factor > knobStartFactor ? " longer" : " shorter") + " by hand (x" + factor.ToString("0.00") + ")");
        }

        // ------------------------------------------------------------ tests

        public bool SimulatePartVoice(string text) => PartVoice(text, (text ?? "").ToLowerInvariant().Trim().TrimEnd('.', '!', '?'));
        public string[] ProductParts => Product != null ? Product.GetComponentInChildren<PaintableThing>()?.Parts : null;
        public PartEdit[] ProductPartEdits => Design?.partEdits;
    }
}
