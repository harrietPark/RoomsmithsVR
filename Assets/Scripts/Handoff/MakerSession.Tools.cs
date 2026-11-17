using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    // F29 phase 2: the hands are the tools.
    //  - Palette: a wooden painter's palette. Turn the left palm up and it sits on the palm; otherwise it
    //    lies on the desk to the person's left. Everything picked is saved (maker_palette.json) and kept
    //    between sessions, so the person's taste builds up.
    //  - Colour: with the palette on the palm, point the right index finger at anything real and pinch
    //    quickly: the colour just past the fingertip (from the passthrough camera) drops into the palette.
    //  - Texture: make an L with both hands around a surface and hold still (MakerSession.Capture.cs).
    //  - "Spark, get the colour of that lamp": Spark looks, walks over and brings its colour and texture.
    //  - A swatch pinched off the palette and dropped on a look (or on Maker) asks for looks in it; on the
    //    see-through or made object it repaints it.
    //  - "Let me draw": Maker hands over its sketchbook, which opens flat on the real desk; draw with a
    //    fingertip on the desk (the desk is the drawing surface), then say "done", give a thumbs up or
    //    touch the green check. Cups, vases, lamps and holders take one side (bottom to top), everything
    //    else the outline seen from above. Maker cleans it up on its own pad.
    //  - Size by hands: flat hands facing each other, held still, show the width between them; held a
    //    second, it is taken as the size to make it. Not while measuring: there only the two-hand pinch
    //    tape measures.
    public sealed partial class MakerSession
    {
        [Serializable] public sealed class Swatch { public string kind, hex, name, image, at; }
        [Serializable] private sealed class PaletteData { public List<Swatch> swatches = new List<Swatch>(); }
        public static string PalettePath => Path.Combine(Application.persistentDataPath, "maker_palette.json");
        private static string PaletteFolder => Path.Combine(Application.persistentDataPath, "palette");
        public const int PaletteSlots = 8;

        public readonly List<Swatch> Palette = new List<Swatch>();
        public bool PaletteOnPalm { get; private set; }
        public Transform PaletteBoard => paletteBoard;
        public int ColourPicks { get; private set; }
        public int TextureFrames { get; private set; }
        public int Fetches { get; private set; }
        public Swatch Dragging => dragSwatch;

        public bool Drawing { get; private set; }
        public string DrawMode { get; private set; } = "outline";   // outline (seen from above) | profile (one side)
        public float[] DrawnOutline { get; private set; }            // 72 radii around the centre, max 1
        public float[] DrawnProfile { get; private set; }            // 32 radii from the bottom up, max 1
        public float DrawnAspect { get; private set; }               // profile: height / widest diameter
        public byte[] DrawingJpeg { get; private set; }
        public int DrawnPoints => drawn.Sum(s => s.Count);
        public Transform DrawingPaper => paper;

        public float HandSizeMm { get; private set; }
        public bool SizingWithHands => sizeBar != null && sizeBar.gameObject.activeSelf;

        private bool paletteLoaded, editorPalm;
        private Transform paletteBoard;
        private readonly List<Transform> swatchViews = new List<Transform>();
        private float palmSince = -1f, palmLostSince = -1f;
        private Swatch dragSwatch;
        private Transform dragView;
        private Hand dragHand;
        private bool mouseDragSwatch, mouseDrawing;
        private Transform paper, paperDone;
        private Mesh paperInk;
        private readonly PageInk pageInk = new PageInk();   // F80: the person's lines on the page (was InkLines)
        private readonly PenFilter penFilter = new PenFilter();
        private readonly List<List<Vector2>> drawn = new List<List<Vector2>>();
        private bool penDown, drawingDone;
        private Hand drawingHand;
        private float sizeCooldown;
        private readonly List<(float t, float d)> sizeSamples = new List<(float, float)>();
        private Transform sizeBar, sizeLabelCard;
        private TextMeshProUGUI sizeLabel;
        private bool sketchOnce;
        private const float PaperSize = .2f;

        // ------------------------------------------------------------ palette data

        private void LoadPalette()
        {
            if (paletteLoaded) return;
            paletteLoaded = true;
            try
            {
                if (File.Exists(PalettePath))
                    Palette.AddRange((JsonConvert.DeserializeObject<PaletteData>(File.ReadAllText(PalettePath)) ?? new PaletteData()).swatches);
            }
            catch (Exception exception) { Debug.LogWarning("[Windups Palette] " + exception.Message); }
        }

        private Swatch AddSwatch(string kind, Color colour, string name, byte[] jpeg = null)
        {
            LoadPalette();
            var swatch = new Swatch { kind = kind, hex = "#" + ColorUtility.ToHtmlStringRGB(colour).ToLowerInvariant(), name = MakerRoles.Clean(name), at = DateTime.Now.ToString("MMM d, HH:mm") };
            if (jpeg != null)
                try
                {
                    Directory.CreateDirectory(PaletteFolder);
                    swatch.image = Path.Combine(PaletteFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".jpg");
                    File.WriteAllBytes(swatch.image, jpeg);
                }
                catch (Exception exception) { swatch.image = null; Debug.LogWarning("[Windups Palette] " + exception.Message); }
            Palette.Add(swatch);
            try { File.WriteAllText(PalettePath, JsonConvert.SerializeObject(new PaletteData { swatches = Palette }, Formatting.Indented)); }
            catch (Exception exception) { Debug.LogWarning("[Windups Palette] " + exception.Message); }
            RebuildSwatches();
            Talk.Add("Palette got " + (kind == "texture" ? "the texture of " : "") + swatch.name + " (" + ColourName(colour) + ", " + swatch.hex + ")");
            Note("palette", (kind == "texture" ? "Texture: " : "Colour: ") + swatch.name, ColourName(colour) + " " + swatch.hex, jpeg);
            InputSwatch(swatch, colour, jpeg); // F33: picked while the colours are being given
            return swatch;
        }

        public static void ForgetPalette()
        {
            try { if (File.Exists(PalettePath)) File.Delete(PalettePath); } catch { }
            try { if (Directory.Exists(PaletteFolder)) Directory.Delete(PaletteFolder, true); } catch { }
        }

        // Plain words for a colour (the AI and the captions use them).
        public static string ColourName(Color c)
        {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            if (v < .18f) return "black";
            if (s < .12f) return v > .85f ? "white" : v > .55f ? "light grey" : "grey";
            string hue = h < .03f || h >= .95f ? "red" : h < .09f ? "orange" : h < .17f ? "yellow" : h < .25f ? "lime" : h < .45f ? "green"
                : h < .55f ? "teal" : h < .7f ? "blue" : h < .8f ? "purple" : "pink";
            if (hue == "orange" && v < .6f) hue = "brown";
            string tone = v < .45f ? "deep " : s < .35f ? "soft " : v > .85f && s < .6f ? "light " : "";
            return tone + hue;
        }

        private static Color Hex(string hex) => ColorUtility.TryParseHtmlString(hex ?? "", out var c) ? c : Color.grey;

        // ------------------------------------------------------------ palette board

        private void EnsurePalette()
        {
            if (paletteBoard != null) return;
            LoadPalette();
            Color wood = new Color(.86f, .68f, .45f), woodEdge = new Color(.68f, .5f, .3f);
            paletteBoard = new GameObject("Palette").transform;
            paletteBoard.SetParent(transform, false);
            // 2026-11-15: the designed wooden palette (MakerSession.PaletteModel.cs); the disc below only without it.
            if (BuildPaletteModel()) { RebuildSwatches(); return; }
            var board = ToyMeshes.Disc(.068f, .005f, wood, woodEdge, "PaletteBoard");
            owned.Add(board);
            var b = Part("Board", board);
            b.SetParent(paletteBoard, false);
            b.localScale = new Vector3(1.25f, 1f, 1f);
            var hole = ToyMeshes.Disc(.011f, .0008f, new Color(.42f, .3f, .2f), new Color(.42f, .3f, .2f), "PaletteHole");
            owned.Add(hole);
            var h = Part("Thumb Hole", hole);
            h.SetParent(paletteBoard, false);
            h.localPosition = new Vector3(.056f, -.03f, -.0006f);
            RebuildSwatches();
        }

        // Eight big paint spots around the rim (the thumb hole sits at the lower right); on the designed palette, its
        // own eight paint blobs.
        private Vector3 SlotPosition(int i)
        {
            if (paintSlots.Count > 0) return ModelSlot(i);
            float[] angles = { 90f, 130f, 170f, 210f, 250f, 290f, 50f, 10f };
            float a = angles[i % angles.Length] * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(a) * .061f, Mathf.Sin(a) * .045f, -.003f);
        }

        private void RebuildSwatches()
        {
            if (paletteBoard == null) return;
            foreach (var v in swatchViews) if (v != null) Destroy(v.gameObject);
            swatchViews.Clear();
            var shown = Palette.Skip(Mathf.Max(0, Palette.Count - PaletteSlots)).ToList();
            for (int i = 0; i < shown.Count; i++)
            {
                var view = SwatchView(shown[i], BlobRadius, i);
                view.SetParent(paletteBoard, false);
                view.localPosition = SlotPosition(i);
                view.localRotation = Quaternion.identity;
                swatchViews.Add(view);
            }
        }

        private readonly Dictionary<string, Texture2D> swatchTextures = new Dictionary<string, Texture2D>();
        private Texture2D LoadSwatchTexture(string path)
        {
            if (swatchTextures.TryGetValue(path, out var t) && t != null) return t;
            try
            {
                t = new Texture2D(2, 2) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear };
                t.LoadImage(File.ReadAllBytes(path));
                owned.Add(t);
                swatchTextures[path] = t;
                return t;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------ every frame (from Update)

        private void ToolsFrame()
        {
            EnsurePalette();
            UpdatePalm();
            PlacePalette();
            CoachFrame();
            SeenFrame();
            UpdateSwatchDrag();
            JiggleFrame(); // F81: deleting paints (MakerSession.SwatchDelete.cs)
            PalettePressFrame(); // 2026-11-15: a fingertip squashes the designed palette's paint blobs
            UpdateBrush();
            if (Drawing) UpdateDrawingHands();
            else
            {
                PutSketchFingersAway(); // F71: the page closed - the ink and the eraser leave the hand
                if (!finderPinned) { UpdateCapture(); UpdateTextureFrame(); }
                // Sizes come from the two-hand pinch tape (and words) only: the flat-palms size gesture is
                // off everywhere (on Quest it kept being taken for the tape).
                if (sizeBar != null && sizeBar.gameObject.activeSelf) { sizeSamples.Clear(); sizeBar.gameObject.SetActive(false); sizeLabelCard.gameObject.SetActive(false); }
            }
        }

        private Hand LiveHand(Handedness side)
        {
            var hand = HandSet.Side(side);
            return hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand) ? hand : null;
        }

        private void UpdatePalm()
        {
            if (HandRules) { UpdatePalmHandOver(); return; } // F66: the palette is handed over by Spark
            bool up = false;
            var left = LiveHand(Handedness.Left);
            if (left != null && Head != null && HandIO.TryPalm(left, out var palm, out var normal) &&
                HandIO.Joint(left, HandJointId.HandIndexTip, out Pose index))
            {
                Vector3 toEye = (Head.position - palm).normalized;
                bool open = Vector3.Distance(index.position, palm) > .065f;
                up = open && !HandIO.Pinching(left) && Vector3.Dot(normal, Vector3.up) > .45f && Vector3.Dot(normal, toEye) > .1f;
            }
            if (editorPalm) up = true;
            // F51: a character stands on the left palm (or is being carried onto it): no palette under it.
            if (PalmPerch.Busy(Handedness.Left)) { up = false; if (PaletteOnPalm) palmLostSince = Mathf.Min(palmLostSince < 0f ? Time.time : palmLostSince, Time.time - .3f); }
            if (up) { palmLostSince = -1f; if (palmSince < 0f) palmSince = Time.time; }
            else { palmSince = -1f; if (palmLostSince < 0f) palmLostSince = Time.time; }
            bool was = PaletteOnPalm;
            if (!PaletteOnPalm && palmSince >= 0f && Time.time - palmSince > .25f) PaletteOnPalm = true;
            if (PaletteOnPalm && palmLostSince >= 0f && Time.time - palmLostSince > .35f) PaletteOnPalm = false;
            if (PaletteOnPalm && !was) ToySounds.Play(ToySounds.Cue.Pop, paletteBoard.position, .35f, 1.3f);
        }

        private void PlacePalette()
        {
            if (HandRules) { PlacePaletteHandOver(); return; } // F66
            float blend = 1f - Mathf.Exp(-Time.deltaTime * 12f);
            Vector3 pos;
            Quaternion rot;
            var left = LiveHand(Handedness.Left);
            if (PaletteOnPalm && !editorPalm && left != null && HandIO.TryPalm(left, out var palm, out var normal) &&
                HandIO.Joint(left, HandJointId.HandMiddle1, out Pose middle))
            {
                Vector3 fingers = Vector3.ProjectOnPlane(middle.position - palm, normal).normalized;
                pos = palm + normal * .03f;
                rot = Quaternion.LookRotation(-normal, fingers);
            }
            else if (PaletteOnPalm)
            {
                pos = FrontPoint(.33f, -.16f, out Vector3 side) - side * .12f;
                Vector3 toEye = Head != null ? (Head.position - pos).normalized : Vector3.back;
                rot = Quaternion.LookRotation(-toEye, Vector3.up);
            }
            else
            {
                // Palm down: the palette is put away (it only lives on the left palm).
                if (paletteBoard.gameObject.activeSelf) { ToyFx.Puff(paletteBoard.position, .008f, 3); paletteBoard.gameObject.SetActive(false); }
                return;
            }
            bool appearing = !paletteBoard.gameObject.activeSelf;
            paletteBoard.gameObject.SetActive(Begun);
            if (appearing) { paletteBoard.SetPositionAndRotation(pos, rot); StartCoroutine(Grow(paletteBoard, 1f, .2f)); }
            else paletteBoard.SetPositionAndRotation(Vector3.Lerp(paletteBoard.position, pos, blend), Quaternion.Slerp(paletteBoard.rotation, rot, blend));
        }

        // ------------------------------------------------------------ colour: point and pinch

        // Pinches the tools care about (before anything else): drag a swatch off the palette, or pick a colour.
        private bool ToolsPinch(Hand hand, Vector3 point, Ray? ray)
        {
            // The fingertip draws; pinches on or by the page do nothing else meanwhile (F60: only there; pinches
            // elsewhere used to vanish for the three minutes a forgotten page stayed open).
            if (Drawing && OnOpenPage(point)) return true;
            // F58: a pinch on the open colour wheel (or by its paint tube) belongs to the wheel; it used to start a room
            // colour pick as well, so one pinch added two blobs.
            if (OnColourWheel(point)) return true;
            int slot = SwatchSlotNear(point);
            if (slot >= 0 && hand != null) { BeginSwatchDrag(slot, hand); return true; }
            // F66: with the hand rules a colour pick is what is left when nothing else took the pinch (OnPinch's end);
            // here it used to take every right-hand pinch while the palette was up, pictures and flags included.
            if (!HandRules && PaletteOnPalm && hand != null && hand.Handedness == Handedness.Right)
            {
                BeginCapture(hand);
                return true;
            }
            return false;
        }

        private int SwatchSlotNear(Vector3 point)
        {
            if (paletteBoard == null || !paletteBoard.gameObject.activeInHierarchy) return -1;
            for (int i = 0; i < swatchViews.Count; i++)
                if (swatchViews[i] != null && Vector3.Distance(swatchViews[i].position, point) < .026f) return i;
            return -1;
        }

        // The colour seen just past the fingertip (the finger itself covers the point right under it).
        // F49: `taken` is the picture taken at the pinch (MakerSession.Capture.cs); without it one is taken now.
        // Every outcome is said and logged (a pick never fails silently).
        // F52: `drop` (the pinch's drop, MakerSession.PickDrop.cs) flies into the palette; a colour already read while
        // the pinch was held is the one kept.
        private IEnumerator SampleColour(Vector3 target, Vector3 fingertip, Vector3 wrist, Vector2? viewport, Pick taken = null, Transform drop = null)
        {
            RoomEyes.Frame frame = null;
            if (taken != null)
            {
                for (float until = Time.time + 2.5f; !taken.done && Time.time < until; ) yield return null;
                frame = taken.frame;
            }
            if (frame == null) yield return Eyes.Capture(f => frame = f);
            if (frame == null)
            {
                PickResult("no picture (" + (Eyes.LastError ?? "camera") + ")", false);
                if (drop != null) { ToyFx.Puff(drop.position, .006f, 2); Destroy(drop.gameObject); }
                Say(Crew.Spark, Eyes.LastError ?? "I can't see right now. Pinch it again in a moment?");
                yield break;
            }
            Vector2 at = taken?.colour != null ? taken.view : viewport ?? PickPoint(frame, target, fingertip, wrist, taken?.fingers);
            var colour = taken?.colour ?? RoomEyes.TrueColourAt(frame, at); // F69: the room's light taken out
            if (!colour.HasValue)
            {
                PickResult("outside the camera view at " + at.ToString("F2"), false);
                if (drop != null) { ToyFx.Puff(drop.position, .006f, 2); Destroy(drop.gameObject); }
                Say(Crew.Spark, "That spot is outside my view. Look right at it and pinch again.");
                yield break;
            }
            ColourPicks++;
            Heard();
            var swatch = AddSwatch("colour", colour.Value, ColourName(colour.Value));
            // F69: what the camera had there, and the light that was taken out (for checking a wrong colour later).
            var seen = RoomEyes.ColourAt(frame, at) ?? colour.Value;
            LastPickSeen = seen;
            PickResult("ok " + swatch.hex + " (" + ColourName(colour.Value) + ") at " + at.ToString("F2") +
                       " | camera #" + ColorUtility.ToHtmlStringRGB(seen).ToLowerInvariant() + ", " + RoomEyes.LightOf(frame).Summary, true);
            SavePickCrop(frame, at, colour.Value, seen);
            yield return ColourArrives(swatch, colour.Value, target, drop);
            ToySounds.Play(ToySounds.Cue.Chime, paletteBoard.position, .35f, 1.7f);
            Ack(Crew.Spark, ColourName(colour.Value) + "!");
            if (!FormFirst) CoachTexture(); // F49: the finish lesson teaches the texture frame in its turn
        }

        // Something Spark brought (colour or texture) flies into its slot.
        private IEnumerator FlyIntoPalette(Swatch swatch, Vector3 from)
        {
            if (swatch.kind == "texture")
            {
                var tile = swatch.image != null && File.Exists(swatch.image) ? LoadSwatchTexture(swatch.image) : null;
                var ball = tile != null ? MaterialBall(tile, BlobRadius * .92f) : ColourBall("Swatch Drop", Hex(swatch.hex), BlobRadius);
                ball.position = from;
                yield return FlyInto(ball, swatch, null, null);
            }
            else
            {
                var drop = ColourBall("Swatch Drop", Hex(swatch.hex), .011f);
                drop.position = from;
                yield return FlyInto(drop, swatch, Hex(swatch.hex), null);
            }
        }

        // ------------------------------------------------------------ texture: the patch the finger held

        private bool finderPinned; // tests: the viewfinder stays as previewed

        public int LastTileSize { get; private set; }
        public Rect LastFrameView { get; private set; }

        // F49: `corners` (the patch on the surface) set what is cut out of the picture, when given.
        private IEnumerator TakeTexture(Vector3 centre, Quaternion facing, float width, float height, Vector2? viewA, Vector2? viewB, bool peel = false, Pose? onto = null, float ontoSize = 0f, Vector3[] patchCorners = null)
        {
            if (Eyes == null || Head == null) yield break;
            // The shutter.
            ToySounds.Play(ToySounds.Cue.Press, centre, .6f, 2.6f);
            StartCoroutine(After(.07f, () => ToySounds.Play(ToySounds.Cue.Press, centre, .5f, 2f)));
            RoomEyes.Frame frame = null;
            yield return Eyes.Capture(f => frame = f);
            if (frame == null) { Caption(Crew.Spark, Eyes.LastError ?? "I can't see right now.", 3f); yield break; }
            Vector2 va, vb;
            if (viewA.HasValue && viewB.HasValue) { va = viewA.Value; vb = viewB.Value; }
            else if (patchCorners != null && patchCorners.Length == 4)
            {
                var seen = patchCorners.Select(c => frame.toViewport(c)).ToList();
                va = new Vector2(seen.Min(c => c.x), seen.Min(c => c.y));
                vb = new Vector2(seen.Max(c => c.x), seen.Max(c => c.y));
            }
            else
            {
                // The frame's four corners as the camera sees them.
                Vector3 side = facing * Vector3.right, up = facing * Vector3.up;
                var corners = new[] { -1f, 1f }.SelectMany(sx => new[] { -1f, 1f }.Select(sy => frame.toViewport(centre + side * (sx * width * .5f) + up * (sy * height * .5f)))).ToList();
                va = new Vector2(corners.Min(c => c.x), corners.Min(c => c.y));
                vb = new Vector2(corners.Max(c => c.x), corners.Max(c => c.y));
            }
            LastFrameView = Rect.MinMaxRect(Mathf.Min(va.x, vb.x), Mathf.Min(va.y, vb.y), Mathf.Max(va.x, vb.x), Mathf.Max(va.y, vb.y));
            var crop = RoomEyes.CropViewport(frame, va, vb, true); // F69: the room's light taken out (colour and level)
            if (crop == null) { Caption(Crew.Spark, "Point a little closer to the middle.", 3f); yield break; }
            // The material, not the photo: a seamless tile with the light flattened out.
            var tileJpeg = MaterialTile.Jpeg(crop) ?? crop;
            var colour = RoomEyes.TrueColourAt(frame, (va + vb) * .5f, Mathf.Abs(va.x - vb.x) * .3f) ?? Color.grey;
            WriteInputLog("texture: " + RoomEyes.LightOf(frame).Summary);
            TextureFrames++;
            Heard();
            var swatch = AddSwatch("texture", colour, ColourName(colour) + " texture", tileJpeg);
            var tile = LoadSwatchTexture(swatch.image);
            LastTileSize = tile != null ? tile.width : 0;
            // The photo goes to the image model, which keeps only the main material; the swatch updates later.
            StartCoroutine(CleanTexture(swatch, crop));
            int toSpark = TexturesToSpark;
            if (onto.HasValue) yield return TextureArrives(swatch, tile, onto.Value.position, onto.Value.rotation, ontoSize, ontoSize, true);
            else yield return TextureArrives(swatch, tile, centre, facing, width, height, peel);
            // F84: framed without the palette, it went into Spark's palette.
            if (TexturesToSpark > toSpark) Ack(Crew.Spark, "Got it! It's on my palette.");
            else Ack(Crew.Spark, Scripted ? "Ooh, nice texture!" : "Ooh, nice texture! Let me clean it up.");
            MarkTaught(TextureTaughtKey);
        }

        // ------------------------------------------------------------ "Spark, get the colour of that lamp"

        public static bool WantsSwatch(string p) =>
            new[] { "colour", "color", "texture", "pattern", "색", "질감", "무늬", "텍스처" }.Any(p.Contains) &&
            new[] { "take", "get", "grab", "pick", "bring", "steal", "copy", "save", "가져", "따", "뽑", "담아", "저장" }.Any(p.Contains);

        private IEnumerator FetchSwatch(string words)
        {
            Ack(Crew.Spark, "On it!");
            RoomEyes.Found found = null;
            yield return SparkLooks(words, list => found = list.FirstOrDefault(f => f.seen.focus) ?? list.FirstOrDefault());
            if (found == null) { Say(Crew.Spark, Eyes.LastError ?? "I can't spot it. Point at it and say that again?"); yield break; }
            if (found.onSurface) yield return Wander(found.basePoint);
            Fetches++;
            string name = MakerRoles.Clean(found.seen.name);
            Color colour = Hex(found.seen.colors?.FirstOrDefault());
            var swatch = AddSwatch("colour", colour, name);
            StartCoroutine(FlyIntoPalette(swatch, found.top));
            if (found.crop != null)
            {
                yield return new WaitForSeconds(.3f);
                var texture = AddSwatch("texture", colour, name + " " + MakerRoles.Clean(found.seen.surface ?? "texture"), MaterialTile.Jpeg(found.crop) ?? found.crop);
                yield return FlyIntoPalette(texture, found.top);
            }
            Say(Crew.Spark, "Got your " + name + "'s " + ColourName(colour) + "! It's in your palette.");
        }

        // ------------------------------------------------------------ using a swatch

        private void BeginSwatchDrag(int slot, Hand hand)
        {
            var shown = Palette.Skip(Mathf.Max(0, Palette.Count - PaletteSlots)).ToList();
            if (slot < 0 || slot >= shown.Count) return;
            dragSwatch = shown[slot];
            dragHand = hand;
            dragStartAt = Time.time;
            dragStartPoint = swatchViews[slot].position;
            dragPinchStart = hand != null ? PinchPoint(hand) : dragStartPoint; // F103: "still" is measured from where the pinch began
            dragView = SwatchView(dragSwatch, BlobRadius);
            ToySounds.Play(ToySounds.Cue.Pop, swatchViews[slot].position, .4f, 1.5f);
            Heard();
        }

        private void UpdateSwatchDrag()
        {
            if (dragSwatch == null || dragView == null) return;
            if (dragHand != null)
            {
                if (!dragHand.isActiveAndEnabled || !HandIO.Connected(dragHand)) { EndSwatchDrag(dragView.position); return; }
                if (SwatchHeldForJiggle()) return; // F81: held 2 s, still: the paints wobble with delete buttons
                Vector3 point = PinchPoint(dragHand);
                dragView.position = point;
                dragView.rotation = SpeechBubbleDock.FacingAt(point);
                if (!HandIO.Pinching(dragHand)) EndSwatchDrag(point);
            }
        }

        private void EndSwatchDrag(Vector3 at)
        {
            var swatch = dragSwatch;
            var hand = dragHand;
            dragSwatch = null;
            dragHand = null;
            mouseDragSwatch = false;
            if (dragView != null) Destroy(dragView.gameObject);
            dragView = null;
            // A quick pinch on the swatch (no drag) loads the brush; a drag drops it somewhere.
            if (SwatchTapped(swatch, hand, at)) return;
            if (swatch != null) DropSwatch(swatch, at, null);
        }

        // Where a swatch lands decides what it does.
        private void DropSwatch(Swatch swatch, Vector3 at, Ray? ray)
        {
            string words = swatch.kind == "texture" ? "the " + swatch.name : swatch.name + " (" + swatch.hex + ")";
            Transform madeThing = Product;
            bool onMade = madeThing != null && (Vector3.Distance(at, madeThing.position) < .1f || (ray.HasValue && RayDistance(ray.Value, madeThing.position) < .06f));
            var picture = PictureAt(at) ?? (ray.HasValue ? PictureOnRay(ray.Value, out _) : null);
            bool onMaker = cast.TryGetValue(Crew.Maker, out var maker) && maker != null &&
                           (Vector3.Distance(at, maker.transform.position + Vector3.up * .1f) < .1f || (ray.HasValue && RayDistance(ray.Value, maker.transform.position + Vector3.up * .1f) < .06f));
            if (onMade && Design != null)
            {
                var d = Design.Copy();
                d.color = swatch.hex;
                Design = d.Clamped();
                if (Product != null && ProductMesh != null)
                {
                    Paint(ProductMesh, Design.Tint(new Color(.95f, .85f, .62f)));
                    Product.GetComponentInChildren<PaintableThing>()?.RefreshColours(ProductMesh);
                }
                ToyFx.Puff(madeThing.position + Vector3.up * .01f, .012f, 5);
                ToySounds.Play(ToySounds.Cue.Chime, madeThing.position, .5f, 1.4f);
                Note("design", "Colour", ColourName(Hex(swatch.hex)) + " " + swatch.hex + " from your palette");
                Say(Crew.Maker, "Painted it " + ColourName(Hex(swatch.hex)) + "!");
                return;
            }
            if (Current == Step.ColourInput) { InputSwatch(swatch, Hex(swatch.hex), swatch.image != null && File.Exists(swatch.image) ? File.ReadAllBytes(swatch.image) : null); return; }
            if (picture != null || onMaker || Current == Step.Style || Current == Step.Looks)
            {
                if (swatch.kind == "texture" && swatch.image != null && File.Exists(swatch.image)) Reference = File.ReadAllBytes(swatch.image);
                Ack(Crew.Maker, "Ooh, that one!");
                string ask = "make it in " + words + (picture?.look != null ? ", like " + picture.look.label : "");
                if (Current == Step.Style || Current == Step.Looks) StepInput(ask, ask.ToLowerInvariant());
                else { Style = (Style + ", " + words).Trim(' ', ','); Say(Crew.Maker, "Noted, " + ColourName(Hex(swatch.hex)) + " it is."); }
                return;
            }
            ToySounds.Play(ToySounds.Cue.Pop, at, .3f, .9f); // dropped nowhere: it goes back to the palette
        }

        // ------------------------------------------------------------ drawing on the real desk

        public static bool WantsToDraw(string p) =>
            new[] { "let me draw", "i'll draw", "i will draw", "i want to draw", "draw it myself", "can i draw", "my own shape", "내가 그릴", "그려볼", "그릴게", "직접 그",
                    "sketchbook", "sketch book", "sketch pad", "drawing pad", "스케치북", "그림 그리고 싶", "내가 그리" }.Any(p.Contains);

        private static bool ProfileThing(string words)
        {
            string w = (words ?? "").ToLowerInvariant();
            return new[] { "cup", "mug", "vase", "pot", "planter", "lamp", "shade", "bowl", "holder", "tumbler", "jar", "컵", "화분", "꽃병", "조명", "그릇", "통" }.Any(w.Contains)
                && !new[] { "coaster", "tray", "mat", "plate", "stand", "clip", "hook", "lid", "코스터", "받침" }.Any(w.Contains);
        }

        private IEnumerator HandSketchbook(bool placedByHand = false)
        {
            if (Drawing || Sketching || SketchbookHeld || takingSketchbook || paperInFlight) yield break;
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null) yield break;
            // F55: only where a drawing means something (the shape, its sketches, its review, v2); else Maker puts it off.
            if (!placedByHand && !DrawingWelcome(out string later)) { DeferDrawing(later); yield break; }
            paperInFlight = true;
            DrawMode = ProfileThing(Need + " " + TargetName) ? "profile" : "outline";
            BuildPaper();
            ClearPaper();
            PutSketchFingersAway();
            if (brushSwatch != null) PutBrushDown(); // F71: the palette's paint and eraser make way for the ink and the thumb eraser
            bool offered = OfferingBook || toolBook; // F55: the book Maker holds up when a hand comes near
            Vector3 from = offered || placedByHand ? paper.position : maker.transform.position + Vector3.up * .08f;
            Quaternion fromRot = offered || placedByHand ? paper.rotation : maker.transform.rotation;
            StopOfferingBook(true);
            StopToolBook(false);
            sketchbookNear.enabled = false;
            if (!offered && !placedByHand) Body(Crew.Maker)?.Play("Decision_Present");
            // F71: the thumb eraser is told once (Settings' "Tips again" brings it back).
            string thumbTip = PlayerPrefs.GetInt(SketchThumbTaughtKey, 0) == 0 ? " Your thumb is the eraser." : "";
            if (thumbTip.Length > 0) { PlayerPrefs.SetInt(SketchThumbTaughtKey, 1); PlayerPrefs.Save(); }
            if (Collecting && !Begun) OfflineLine(Crew.Maker, "Draw anything you like!" + thumbTip); // F78 airplane mode
            else if (Current == Step.ShapeInput) Say(Crew.Maker, "Draw on the page, any way you like." + thumbTip + " Say done when it's ready.");
            else Say(Crew.Maker, (DrawMode == "profile" ? "Here! Draw one side of it on the desk, bottom to top." : "Here! Draw its outline on the desk.") + thumbTip);
            Vector3 spot = placedByHand ? from : DeskPointInFront() + Vector3.up * .002f;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - spot, Vector3.up).normalized : Vector3.back;
            if (toUser.sqrMagnitude < .001f) toUser = Vector3.back;
            Quaternion lying = placedByHand ? fromRot : Quaternion.LookRotation(Vector3.down, -toUser);
            if (placedByHand && SurfaceAt(from) is { } desk)
            {
                float y = desk.Point(new Vector2(from.x, from.z)).y;
                if (Mathf.Abs(from.y - y) < .06f)
                {
                    spot.y = y + .002f;
                    lying = Quaternion.LookRotation(Vector3.down, -toUser);
                }
            }
            paper.gameObject.SetActive(true);
            float startScale = placedByHand ? paper.localScale.x : .3f;
            for (float t = 0f; t < 1f; t += Time.deltaTime / (placedByHand ? .25f : .55f))
            {
                paper.position = Vector3.Lerp(from, spot, Mathf.SmoothStep(0f, 1f, t));
                if (!placedByHand) paper.position += Vector3.up * Mathf.Sin(t * Mathf.PI) * .12f;
                paper.rotation = Quaternion.Slerp(fromRot, lying, t);
                paper.localScale = Vector3.one * Mathf.Lerp(startScale, 1f, t);
                yield return null;
            }
            paper.SetPositionAndRotation(spot, lying);
            paper.localScale = Vector3.one;
            ToySounds.Play(ToySounds.Cue.Paper, spot, .6f, 1f);
            Drawing = true;
            drawingDone = false;
            paperInFlight = false;
            SketchbookGrip(false);
            Heard();
            // F60: an empty page goes back after EmptyPageClose; a drawing left alone for DrawingIdleClose is kept in
            // the notebook only (it used to be taken after 180 s whatever it was, and an empty page redrew the sketches).
            float opened = Time.time, inkAt = Time.time;
            int ink = DrawnPoints;
            bool empty = false;
            drawingTimedOut = false;
            while (true)
            {
                while (Drawing && !drawingDone)
                {
                    if (DrawnPoints != ink || penDown || SketchbookHeld) { ink = DrawnPoints; inkAt = Time.time; }
                    if (DrawnPoints < 8 && Time.time - Mathf.Max(opened, inkAt) > EmptyPageClose) { empty = true; break; }
                    if (DrawnPoints >= 8 && Time.time - inkAt > DrawingIdleClose) { drawingTimedOut = true; break; }
                    yield return null;
                }
                if (!Drawing) yield break;
                if (empty || DrawnPoints < 8) break;
                // F84 (the user: "bring back Maker asking to refine the sketch: Refine or Done"): asked on the open page; drawing
                // on closes the question and the page is the person's again until the next check.
                if (drawingTimedOut || !RefineAsk || (Collecting && !Begun)) break; // (F78 airplane mode keeps the page as drawn)
                if (PolishOnGive && GiveCardsToMaker && Current == Step.ShapeInput) break; // F86: asked when the card is handed to Maker
                yield return AskRefine();
                if (!Drawing) yield break;
                if (!refineKeepDrawing) break;
                drawingDone = false;
                ink = DrawnPoints;
                inkAt = Time.time;
            }
            sketchbookNear.enabled = false;
            if (empty || DrawnPoints < 8) { CloseUnusedBook(empty ? "nothing drawn for " + EmptyPageClose + " s" : "empty page"); yield break; }
            // F77: the lines were tidied as they were drawn (EndStroke); Maker no longer offers to tidy them here. The
            // check's press is seen (down, lit, ripple) before the page is taken (the tidy question used to give it time).
            if (!drawingTimedOut && !TidyOnLift && !RefineAsk && !(Collecting && !Begun)) yield return OfferTidy();
            else for (float t = 0f; t < .35f && Drawing; t += Time.deltaTime) yield return null;
            if (!Drawing) yield break;
            yield return TakeDrawing(maker);
        }

        private void BuildPaper()
        {
            if (paper != null) return;
            BuildSketchbookGrip();
            var sheet = ToyMeshes.RoundedSlab(PaperSize, PaperSize, .008f, .002f, new Color(1f, .99f, .96f), new Color(.88f, .86f, .8f), new Color(1f, .99f, .96f), "DrawingPaper");
            owned.Add(sheet);
            Part(paper, "Sheet", sheet, Vector3.zero, 0f);
            var cover = ToyMeshes.RoundedSlab(PaperSize + .01f, PaperSize + .01f, .01f, .002f, MakerRoles.Color(Crew.Maker), Color.Lerp(MakerRoles.Color(Crew.Maker), Color.black, .3f), MakerRoles.Color(Crew.Maker), "DrawingCover");
            owned.Add(cover);
            Part(paper, "Cover", cover, new Vector3(0f, 0f, .002f), 0f);
            // Faint guides: a centre mark (outline) and the axis (profile) are added per mode in ClearPaper.
            var ink = new GameObject("Ink", typeof(MeshFilter), typeof(MeshRenderer));
            ink.transform.SetParent(paper, false);
            paperInk = new Mesh { name = "DrawingInk" };
            paperInk.MarkDynamic();
            owned.Add(paperInk);
            ink.GetComponent<MeshFilter>().sharedMesh = paperInk;
            ink.GetComponent<MeshRenderer>().sharedMaterial = material;
            // F71: the undo (lower left) and the green check (lower right), raised buttons (MakerSession.SketchFingers.cs).
            BuildPageButtons();
            paper.gameObject.SetActive(false);
        }

        private Transform guide;
        private void ClearPaper()
        {
            drawn.Clear();
            pageHistory.Clear(); // F71 (ClearPageKeepUndo keeps the cleared page for undo)
            StopRub();
            penDown = false;
            pageInk.Clear();
            pageInk.Apply(paperInk);
            if (guide != null) Destroy(guide.gameObject);
            guide = new GameObject("Guide").transform;
            guide.SetParent(paper, false);
            Color faint = new Color(.82f, .84f, .9f);
            var bar = StickerMesh("guideBar", () => ToyMeshes.RoundedSlab(.0012f, PaperSize * .8f, .0005f, .0004f, faint, faint, faint, "GuideBar"));
            var dot = StickerMesh("guideDot", () => ToyMeshes.Disc(.0025f, .0004f, faint, faint, "GuideDot"));
            if (DrawMode == "profile") Part(guide, "Axis", bar, new Vector3(0f, 0f, -.0006f), 0f);
            else Part(guide, "Centre", dot, new Vector3(0f, 0f, -.0006f), 0f);
        }

        private void UpdateDrawingHands()
        {
            if (paper == null) return;
            // F71: the hand at the page wears the ink (index) and the eraser (thumb), MakerSession.SketchFingers.cs.
            SketchFingersFrame();
            // F104 (Quest 2026-11-16, the user: "while I lift the sketchpad the pen should be off"): while the page is held
            // or being taken, and until the hands have let go and lifted off it, nothing draws or rubs out; a line begun
            // just before the page was taken (the fingers closing on its rim) is taken back.
            if (PenQuietForPage()) { penDown = false; drawingHand = null; StopRub(); PenDot(Vector2.zero, false, false); PageButtonsFrame(false); return; }
            if (drawingDone) { StopRub(); PenDot(Vector2.zero, false, false); PageButtonsFrame(false); return; }
            // A thumbs up finishes.
            foreach (var h in HandSet.Both())
                if (h != null && h.isActiveAndEnabled && HandIO.Connected(h) && HandIO.ThumbsUp(h)) { FinishDrawing(); return; }
            // F68 -> F71 (Quest 2026-11-14: "the check doesn't feel like a button, no sign it was pressed"): the undo and the
            // check are raised buttons that sink under a fingertip and click, light up and ripple when pressed.
            if (PageButtonsFrame(!penDown && !erasing)) return;
            // F71: only the pen hand draws (index) and rubs out (thumb).
            Hand drawing = null, hand = pageHand;
            Vector2 at = Vector2.zero, hoverAt = Vector2.zero;
            bool hovering = false;
            float indexHeight = float.MaxValue;
            if (hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand) && !HandIO.Pinching(hand) && HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip))
            {
                Vector3 local = paper.InverseTransformPoint(tip.position);
                indexHeight = -local.z;
                // F80 (Quest: "the sketchbook undo button doesn't work well"): a fingertip coming down onto a button drew a dot
                // first, and a line touching a button is not a press - so the press was lost (and undo then took back that
                // dot). No line starts within ButtonGuard of a button; a line already going stops at the button as before.
                bool onButton = penDown ? OnPageButton(tip.position) : NearPageButton(local);
                bool overPage = Mathf.Abs(local.x) < PaperSize * .5f && Mathf.Abs(local.y) < PaperSize * .5f && !onButton;
                // F68 (Quest 2026-11-14: "drawing in the sketchbook is too hard"): the pen dot shows where the fingertip is
                // over the page; the fingertip draws from PenAbove over the page (it was 1.4 cm) to just under it.
                if (overPage && local.z > -PenHover && local.z < .01f) { hovering = true; hoverAt = new Vector2(local.x, local.y); }
                // The page faces up (its front is local -z): the fingertip on or just above the desk draws (not on a button).
                if (overPage && local.z > -PenAbove && local.z < .01f) { drawing = hand; at = new Vector2(local.x, local.y); }
            }
            // F71: the thumb rubs out while it is lower than the index (then the index draws nothing); a line goes on until
            // the index leaves the page, a rub until the thumb leaves it.
            if (penDown) StopRub();
            else if (ThumbRub(hand, indexHeight)) { drawing = null; hovering = false; }
            PenDot(drawing != null ? at : hoverAt, hovering || drawing != null, drawing != null);
            if (drawing == null) { EndStroke(); drawingHand = null; return; } // F77: the line ends and is tidied
            // F68 -> F80: a steadier line (hand tracking shakes by a few millimetres): a speed-aware filter (One Euro) - slow
            // strokes are smoothed hard, fast ones follow the fingertip closely (MakerSession.PageInk.cs).
            if (!penDown) penFilter.Reset();
            at = penFilter.Step(at, Time.time);
            Pen(at, drawing);
        }

        // F68: the pen dot over the page (where a line would start), dark and bigger while drawing.
        public const float PenAbove = .025f, PenHover = .07f, PenSmooth = .55f, DoneReach = .022f;
        private Transform penDot;
        public bool PenDotShown => penDot != null && penDot.gameObject.activeSelf;
        private void PenDot(Vector2 at, bool show, bool down)
        {
            if (paper == null) return;
            if (penDot == null)
            {
                penDot = new GameObject("Pen Dot").transform;
                penDot.SetParent(paper, false);
                Part(penDot, "Disc", StickerMesh("penDot", () => ToyMeshes.Disc(.0035f, .0006f, new Color(.25f, .27f, .35f), new Color(.25f, .27f, .35f), "PenDot")), Vector3.zero, 0f);
            }
            penDot.gameObject.SetActive(show);
            if (!show) return;
            penDot.localPosition = new Vector3(at.x, at.y, -.0012f);
            penDot.localScale = Vector3.one * (down ? 1.25f : .8f);
        }

        private void Pen(Vector2 at, Hand hand)
        {
            if (!penDown)
            {
                KeepPageForUndo(); // F71: each line is one undo step
                penDown = true;
                drawingHand = hand;
                drawn.Add(new List<Vector2> { at });
                penMovedAt = Time.time; // F81
                pageInk.Rebuild(drawn, true); // F80: the earlier lines are kept as they are, this one is drawn live
                pageInk.Apply(paperInk);
                Heard();
                return;
            }
            var stroke = drawn[drawn.Count - 1];
            if (Vector2.Distance(stroke[stroke.Count - 1], at) < .0015f) return;
            stroke.Add(at);
            penMovedAt = Time.time; // F81: the fingertip still moving (a rest before lifting = QuickShape)
            pageInk.UpdateLast(stroke, true);
            pageInk.Apply(paperInk);
            if (stroke.Count % 6 == 0) ToySounds.Play(ToySounds.Cue.Paper, paper.position, .06f, 2.6f);
        }

        public void FinishDrawing()
        {
            if (!Drawing) return;
            if (DrawnPoints < 8) { Ack(Crew.Maker, "Draw a bit more first!"); return; }
            drawingDone = true;
        }

        private IEnumerator TakeDrawing(WindupCharacter maker)
        {
            // F60: an empty page is never a drawing.
            if (DrawnPoints < 8) { CloseUnusedBook("empty page"); yield break; }
            if (Collecting && !Begun) { yield return CollectDrawing(maker); yield break; } // F78 airplane mode: kept as it is
            // F56: in the shape step the page becomes a drawing card (MakerSession.ShapeCards.cs).
            if (Current == Step.ShapeInput) { drawingTimedOut = false; yield return DrawingToCard(maker); yield break; }
            paperInFlight = true;
            Drawing = false;
            penDown = false;
            var points = drawn.SelectMany(s => s).ToList();
            if (DrawMode == "profile") ProfileFrom(drawn); else OutlineFrom(points);
            DrawingJpeg = RasterDrawing(drawn);
            Note("design", DrawMode == "profile" ? "Your side shape" : "Your outline", "Drawn by you on the desk.", DrawingJpeg);
            Talk.Add("The person drew " + (DrawMode == "profile" ? "one side of the object (bottom to top)" : "the outline seen from above") + ".");
            // Maker takes the page back and cleans it up on its own pad.
            Vector3 from = paper.position;
            Quaternion fromRot = paper.rotation;
            for (float t = 0f; t < 1f; t += Time.deltaTime / .45f)
            {
                paper.position = Vector3.Lerp(from, maker.transform.position + Vector3.up * .08f, Mathf.SmoothStep(0f, 1f, t)) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .1f;
                paper.rotation = Quaternion.Slerp(fromRot, maker.transform.rotation, t);
                paper.localScale = Vector3.one * Mathf.Lerp(1f, .25f, t);
                yield return null;
            }
            paper.gameObject.SetActive(false);
            paperInFlight = false;
            // F60: a page left alone is kept, but it changes nothing (no redraw, no flag, no shape).
            if (drawingTimedOut)
            {
                drawingTimedOut = false;
                Say(Crew.Maker, "I kept your drawing in my notebook.");
                yield break;
            }
            Ack(Crew.Maker, "Nice! Let me clean it up.");
            Shape = DrawMode == "profile" ? "your own drawn side shape" : "your own drawn outline";
            sketchOnce = true;
            if (!Sketching) StartCoroutine(Sketch());
            float until = Time.time + 20f;
            while ((sketchOnce || Sketching) && Time.time < until) yield return null;
            // At the shape step (or once something is planned or made) the drawing is planned right away
            // (while it is being worked out or built: right after it).
            if (UseDrawing()) { }                     // F55: the shape sketches again, or a flag on the next version
            else if (Current == Step.Shape || Current == Step.Planning || Current == Step.Making || Current == Step.Done)
            {
                Say(Crew.Maker, "Got your shape!");
                StepInput(Shape, Shape.ToLowerInvariant());
            }
            else Say(Crew.Maker, "Got your shape. I'll use it.");
        }

        // Outline seen from above: radius by angle around the centre (72 steps), smoothed, max 1.
        private void OutlineFrom(List<Vector2> points)
        {
            if (points.Count < 3) return;
            Vector2 c = points.Aggregate(Vector2.zero, (a, b) => a + b) / points.Count;
            var radii = new float[72];
            var hits = new float[72];
            foreach (var p in points)
            {
                Vector2 d = p - c;
                int i = Mathf.RoundToInt(Mathf.Repeat(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, 360f) / 5f) % 72;
                radii[i] = Mathf.Max(radii[i], d.magnitude);
                hits[i] = 1f;
            }
            // Fill gaps by interpolating around the circle, then smooth.
            for (int i = 0; i < 72; i++)
            {
                if (hits[i] > 0f) continue;
                int a = i, b = i;
                for (int k = 1; k < 72 && hits[a] == 0f; k++) a = (i - k + 72) % 72;
                for (int k = 1; k < 72 && hits[b] == 0f; k++) b = (i + k) % 72;
                int da = (i - a + 72) % 72, db = (b - i + 72) % 72;
                radii[i] = da + db == 0 ? radii[a] : Mathf.Lerp(radii[a], radii[b], da / (float)(da + db));
            }
            var smooth = new float[72];
            for (int i = 0; i < 72; i++) smooth[i] = (radii[(i + 70) % 72] + radii[(i + 71) % 72] * 2f + radii[i] * 3f + radii[(i + 1) % 72] * 2f + radii[(i + 2) % 72]) / 9f;
            float max = smooth.Max();
            DrawnOutline = max > 0f ? smooth.Select(r => r / max).ToArray() : null;
        }

        // One side, bottom to top: the distance from the axis at 32 heights, smoothed, max 1.
        private void ProfileFrom(List<List<Vector2>> strokes)
        {
            var points = strokes.SelectMany(s => s).ToList();
            if (points.Count < 3) return;
            // The page's +y points away from the person: bottom of the object = nearest the person (lowest y).
            float y0 = points.Min(p => p.y), y1 = points.Max(p => p.y);
            if (y1 - y0 < .02f) return;
            var radii = new float[32];
            for (int i = 0; i < 32; i++)
            {
                float y = Mathf.Lerp(y0, y1, i / 31f);
                var near = points.Where(p => Mathf.Abs(p.y - y) < (y1 - y0) / 40f + .002f).ToList();
                radii[i] = near.Count > 0 ? near.Max(p => Mathf.Abs(p.x)) : -1f;
            }
            for (int i = 0; i < 32; i++)
                if (radii[i] < 0f)
                {
                    int a = i, b = i;
                    while (a > 0 && radii[a] < 0f) a--;
                    while (b < 31 && radii[b] < 0f) b++;
                    radii[i] = radii[a] >= 0f && radii[b] >= 0f ? Mathf.Lerp(radii[a], radii[b], (i - a) / (float)Mathf.Max(1, b - a)) : Mathf.Max(radii[a], radii[b], 0f);
                }
            var smooth = radii.Select((r, i) => (radii[Mathf.Max(0, i - 1)] + r * 2f + radii[Mathf.Min(31, i + 1)]) / 4f).ToArray();
            float max = smooth.Max();
            if (max <= 0f) return;
            DrawnProfile = smooth.Select(r => Mathf.Max(.05f, r / max)).ToArray();
            DrawnAspect = (y1 - y0) / (max * 2f);
        }

        // The person's drawing as a small picture (for the notebook and as a reference for the looks).
        private static byte[] RasterDrawing(List<List<Vector2>> strokes)
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            var pixels = Enumerable.Repeat(new Color32(253, 252, 245, 255), size * size).ToArray();
            Color32 ink = new Color32(60, 64, 80, 255);
            Vector2 Px(Vector2 p) => new Vector2((p.x / PaperSize + .5f) * (size - 1), (p.y / PaperSize + .5f) * (size - 1));
            foreach (var stroke in strokes)
                for (int i = 0; i < stroke.Count; i++)
                {
                    Vector2 a = Px(stroke[Mathf.Max(0, i - 1)]), b = Px(stroke[i]);
                    int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b)));
                    for (int k = 0; k <= steps; k++)
                    {
                        Vector2 p = Vector2.Lerp(a, b, k / (float)steps);
                        for (int dx = -2; dx <= 2; dx++)
                            for (int dy = -2; dy <= 2; dy++)
                            {
                                int x = Mathf.RoundToInt(p.x) + dx, y = Mathf.RoundToInt(p.y) + dy;
                                if (x >= 0 && y >= 0 && x < size && y < size && dx * dx + dy * dy <= 5) pixels[y * size + x] = ink;
                            }
                    }
                }
            tex.SetPixels32(pixels);
            tex.Apply();
            var jpeg = tex.EncodeToJPG(85);
            UnityEngine.Object.Destroy(tex);
            return jpeg;
        }

        // Maker's clean copy of the person's side shape: the silhouette (mirrored), the top rim and a size line.
        private List<PenStroke> ProfilePlan()
        {
            var plan = new List<PenStroke>();
            float aspect = Mathf.Max(.25f, DrawnAspect);
            float h = .03f, r = Mathf.Min(.022f, h / (aspect * 2f));
            if (r >= .022f) h = r * 2f * aspect;
            Vector2 c = new Vector2(0f, .003f);
            var outline = new PenStroke { width = .0015f };
            for (int i = 0; i < 32; i++) outline.points.Add(c + new Vector2(DrawnProfile[i] * r, -h * .5f + h * i / 31f));
            for (int i = 31; i >= 0; i--) outline.points.Add(c + new Vector2(-DrawnProfile[i] * r, -h * .5f + h * i / 31f));
            outline.points.Add(outline.points[0]);
            plan.Add(outline);
            var rim = new PenStroke { width = .001f };
            float rt = DrawnProfile[31] * r;
            for (int k = 0; k <= 24; k++) { float a = k / 24f * Mathf.PI * 2f; rim.points.Add(c + new Vector2(Mathf.Cos(a) * rt, h * .5f + Mathf.Sin(a) * rt * .25f)); }
            plan.Add(rim);
            float y = c.y - h * .5f - .005f;
            PenStroke Line(Vector2 a, Vector2 b) { var s = new PenStroke { width = .0009f }; for (int k = 0; k <= 12; k++) s.points.Add(Vector2.Lerp(a, b, k / 12f)); return s; }
            plan.Add(Line(new Vector2(-r, y), new Vector2(r, y)));
            plan.Add(Line(new Vector2(-r, y - .0016f), new Vector2(-r, y + .0016f)));
            plan.Add(Line(new Vector2(r, y - .0016f), new Vector2(r, y + .0016f)));
            return plan;
        }

        // ------------------------------------------------------------ size by hands

        private void UpdateHandSize()
        {
            var left = LiveHand(Handedness.Left);
            var right = LiveHand(Handedness.Right);
            bool valid = false;
            float d = 0f;
            Vector3 a = Vector3.zero, b = Vector3.zero;
            if (Time.time > sizeCooldown && !Holding && dragSwatch == null && left != null && right != null &&
                !HandIO.Pinching(left) && !HandIO.Pinching(right) &&
                HandIO.TryPalm(left, out a, out var nl) && HandIO.TryPalm(right, out b, out var backR) &&
                HandIO.Joint(left, HandJointId.HandIndexTip, out Pose li) && HandIO.Joint(right, HandJointId.HandIndexTip, out Pose ri))
            {
                Vector3 across = (b - a).normalized;
                Vector3 nr = -backR; // TryPalm's normal is out of the left palm but out of the back of the right hand
                bool facing = Vector3.Dot(nl, nr) < -.6f && Vector3.Dot(nl, across) > .55f && Vector3.Dot(nr, -across) > .55f;
                bool flat = Vector3.Distance(li.position, a) > .07f && Vector3.Distance(ri.position, b) > .07f;
                d = Vector3.Distance(a, b) - .02f; // palm surfaces, not centres
                valid = facing && flat && d > .03f && d < .6f;
            }
            if (!valid)
            {
                sizeSamples.Clear();
                if (sizeBar != null && sizeBar.gameObject.activeSelf && Time.time > sizeCooldown) sizeBar.gameObject.SetActive(false);
                return;
            }
            sizeSamples.Add((Time.time, d));
            sizeSamples.RemoveAll(s => Time.time - s.t > 1f);
            ShowSizeBar(a, b, d);
            float spread = sizeSamples.Max(s => s.d) - sizeSamples.Min(s => s.d);
            if (sizeSamples.Count > 20 && Time.time - sizeSamples[0].t > .9f && spread < .006f) TakeHandSize(sizeSamples.Average(s => s.d) * 1000f, (a + b) * .5f);
        }

        private void ShowSizeBar(Vector3 a, Vector3 b, float d)
        {
            if (sizeBar == null)
            {
                var bar = ToyMeshes.RoundedSlab(1f, .008f, .002f, .002f, new Color(1f, .82f, .2f), new Color(.85f, .62f, .1f), new Color(1f, .82f, .2f), "SizeBar");
                owned.Add(bar);
                sizeBar = Part("Hand Size", bar);
                sizeLabelCard = Panel("Hand Size Label", .06f, .022f, Color.white, out sizeLabel, 11f);
            }
            sizeBar.gameObject.SetActive(true);
            sizeLabelCard.gameObject.SetActive(true);
            Vector3 mid = (a + b) * .5f;
            Vector3 toEye = Head != null ? (Head.position - mid).normalized : Vector3.back;
            Vector3 across = (b - a).normalized;
            // The bar's length (local x) runs from palm to palm; its face looks at the person.
            sizeBar.SetPositionAndRotation(mid, Quaternion.LookRotation(-toEye, Vector3.Cross(-toEye, across)));
            sizeBar.localScale = new Vector3(Mathf.Max(.01f, d), 1f, 1f);
            sizeLabel.text = "<b>" + Mathf.Round(d * 1000f) + " mm</b>";
            sizeLabelCard.SetPositionAndRotation(mid + Vector3.up * .035f, SpeechBubbleDock.FacingAt(mid + Vector3.up * .035f));
        }

        private void TakeHandSize(float mm, Vector3 at)
        {
            sizeSamples.Clear();
            sizeCooldown = Time.time + 3f;
            HandSizeMm = Mathf.Round(mm);
            Heard();
            ToySounds.Play(ToySounds.Cue.Chime, at, .6f, 1.3f);
            ToyFx.Puff(at, .01f, 4);
            StartCoroutine(HideSizeBarLater());
            Note("measure", "Size from your hands", HandSizeMm.ToString("0") + " mm");
            Talk.Add("The person showed a size with their hands: " + HandSizeMm.ToString("0") + " mm");
            if ((Current == Step.Planning || Current == Step.Making || Current == Step.Done) && Design != null)
            {
                Say(Crew.Fixer, "About " + HandSizeMm.ToString("0") + " millimetres across. Resizing.");
                string ask = "change the size to about " + HandSizeMm.ToString("0") + " mm across";
                StepInput(ask, ask);
                return;
            }
            Say(Crew.Fixer, "About " + HandSizeMm.ToString("0") + " millimetres. Noted.");
        }

        private IEnumerator HideSizeBarLater()
        {
            yield return new WaitForSeconds(1.6f);
            if (sizeBar != null && sizeSamples.Count == 0) { sizeBar.gameObject.SetActive(false); sizeLabelCard.gameObject.SetActive(false); }
        }

        // ------------------------------------------------------------ voice

        // Words for the page in hand (done, undo, a fresh page) and the answer to "tidy the lines?". F55: Route asks
        // this first, so "done" while drawing in the shape review finishes the drawing instead of the review.
        private bool DrawingVoice(string p)
        {
            if (RefineVoice(p)) return true; // F84: Refine / Done / Undo on the open page
            if (TidyVoice(p)) return true;
            // F60: "that's all" / "done" while the page is open: finished if something is drawn, else the book goes back.
            if (Drawing && (DoneWords(p) || p.Contains("that's all") || p.Contains("all done") || p.Contains("i'm done")))
            {
                if (DrawnPoints >= 8) FinishDrawing(); else CloseUnusedBook("said done with nothing drawn");
                return true;
            }
            if (Drawing && (p == "done" || p.Contains("that's it") || p.Contains("finished") || p.Contains("다 했") || p.Contains("완성") || p.Contains("끝"))) { FinishDrawing(); return true; }
            if (Drawing && (p.Contains("undo") || p.Contains("erase that") || p.Contains("last line") || p.Contains("되돌") || p.Contains("취소") || p.Contains("한 획"))) { UndoStroke(); return true; }
            if (Drawing && (p.Contains("again") || p.Contains("clear") || p.Contains("erase all") || p.Contains("다시") || p.Contains("다 지워") || p.Contains("지워"))) { ClearPageKeepUndo(); Ack(Crew.Maker, "Fresh page!"); return true; }
            return false;
        }

        private bool ToolsVoice(string text, string p)
        {
            if (DrawingVoice(p)) return true;
            if (PaintVoice(p)) return true;
            if (WantsToDraw(p)) { StartCoroutine(HandSketchbook()); return true; }
            if (WantsSwatch(p)) { StartCoroutine(FetchSwatch(text)); return true; }
            if (p.Contains("my palette") || p.Contains("내 팔레트"))
            {
                LoadPalette();
                if (Palette.Count == 0) { Say(Crew.Spark, "Your palette is empty. Turn your left palm up and pinch any colour!"); return true; }
                string colours = string.Join(", ", Palette.Skip(Mathf.Max(0, Palette.Count - 4)).Select(s => s.name + " " + s.hex));
                string ask = "use my palette: " + colours;
                if (Current == Step.Style || Current == Step.Looks || Current == Step.ColourInput) return StepInput(ask, ask);
                Style = (Style + ", " + ask).Trim(' ', ',');
                Say(Crew.Spark, "Your palette it is!");
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------ Editor and tests

#if UNITY_EDITOR
        // B: palette in hand (then click anything to pick its colour, drag a swatch to use it), T: frame a
        // texture in the middle of the view, Z: show 120 mm with the hands, D: let me draw (drag on the page,
        // Enter when done).
        private bool EditorTools(UnityEngine.InputSystem.Mouse mouse, Ray ray)
        {
            var keys = UnityEngine.InputSystem.Keyboard.current;
            if (keys != null)
            {
                if (keys.bKey.wasPressedThisFrame) editorPalm = !editorPalm;
                if (keys.tKey.wasPressedThisFrame) StartCoroutine(TakeTexture(Head.position + Head.forward * .5f, Quaternion.LookRotation(Head.forward, Vector3.up), .16f, .12f, new Vector2(.4f, .35f), new Vector2(.6f, .6f)));
                if (keys.zKey.wasPressedThisFrame) TakeHandSize(120f, FrontPoint(.35f, -.15f, out _));
                if (keys.dKey.wasPressedThisFrame) StartCoroutine(HandSketchbook());
                if (Drawing && keys.enterKey.wasPressedThisFrame) { FinishDrawing(); return true; }
            }
            if (mouse == null) return false;
            if (Drawing && paper != null)
            {
                var plane = new Plane(-paper.forward, paper.position);
                bool rub = mouse.rightButton.isPressed; // F71: the right button is the thumb eraser in the Editor
                if ((mouse.leftButton.isPressed || rub) && plane.Raycast(ray, out float d))
                {
                    Vector3 local = paper.InverseTransformPoint(ray.GetPoint(d));
                    if (Mathf.Abs(local.x) < PaperSize * .5f && Mathf.Abs(local.y) < PaperSize * .5f)
                    {
                        if (!rub && Vector3.Distance(ray.GetPoint(d), paperDone.position) < .016f) { if (mouse.leftButton.wasPressedThisFrame) PressPaperDone(); return true; }
                        if (!rub && paperUndo != null && Vector3.Distance(ray.GetPoint(d), paperUndo.position) < .016f) { if (mouse.leftButton.wasPressedThisFrame) PressPaperUndo(); return true; }
                        if (!mouseDrawing) { penDown = false; mouseDrawing = true; }
                        if (rub) { if (Time.time >= nextRubAt) { nextRubAt = Time.time + .033f; RubOut(new Vector2(local.x, local.y)); } }
                        else Pen(new Vector2(local.x, local.y), null);
                        return true;
                    }
                }
                if (!mouse.leftButton.isPressed && !rub && mouseDrawing) { mouseDrawing = false; EndStroke(); penDown = false; StopRub(); } // F77: tidied on lift
                return mouseDrawing;
            }
            if (mouseDragSwatch && dragView != null)
            {
                var plane = new Plane(-ray.direction, dragView.position);
                if (plane.Raycast(ray, out float d)) dragView.position = ray.GetPoint(d);
                if (!mouse.leftButton.isPressed)
                {
                    var swatch = dragSwatch;
                    dragSwatch = null;
                    mouseDragSwatch = false;
                    Vector3 at = dragView.position;
                    Destroy(dragView.gameObject);
                    dragView = null;
                    DropSwatch(swatch, at, ray);
                }
                return true;
            }
            if (!mouse.leftButton.wasPressedThisFrame) return false;
            for (int i = 0; i < swatchViews.Count; i++)
                if (swatchViews[i] != null && RayDistance(ray, swatchViews[i].position) < .012f)
                {
                    BeginSwatchDrag(i, null);
                    mouseDragSwatch = true;
                    return true;
                }
            if (PaletteOnPalm)
            {
                var room = arrival.handoff.meeting.environment;
                Vector3 target = Physics.Raycast(ray, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore) ? hit.point : ray.GetPoint(.8f);
                var camera = Camera.main;
                Vector2 view = camera != null ? (Vector2)camera.WorldToViewportPoint(target) : new Vector2(.5f, .5f);
                StartCoroutine(SampleColour(target, target, target, view));
                return true;
            }
            return false;
        }
#endif

        public void SimulatePickColour(Vector3 target, Vector2 viewport) => StartCoroutine(SampleColour(target, target, target, viewport));
        public void SimulateTexture(Vector2 a, Vector2 b) => StartCoroutine(TakeTexture(Head.position + Head.forward * .5f, Quaternion.LookRotation(Head.forward, Vector3.up), .16f, .12f, a, b));

        // Tests: a frame the hands would make (centre, facing the person, size) - captured like the real one.
        public void SimulateFrame(Vector3 centre, float width, float height)
        {
            var facing = Quaternion.LookRotation((centre - Head.position).normalized, Vector3.up);
            StartCoroutine(TakeTexture(centre, facing, width, height, null, null));
        }

        public void PreviewFinder(Vector3 centre, float width, float height, float progress)
        {
            finderPinned = true;
            ShowFramePreview(centre, Quaternion.LookRotation((centre - Head.position).normalized, Vector3.up), width, height, progress);
        }
        public void HideFinder() { finderPinned = false; HideFramePreview(); }
        public void SimulateHandSize(float mm) => TakeHandSize(mm, FrontPoint(.35f, -.15f, out _));
        public void SimulateDropSwatch(int index, Vector3 at) { LoadPalette(); if (index >= 0 && index < Palette.Count) DropSwatch(Palette[index], at, null); }
        public void SimulateDraw(List<List<Vector2>> strokes)
        {
            foreach (var stroke in strokes)
            {
                penDown = false;
                foreach (var p in stroke) Pen(p, null);
            }
            penDown = false;
        }
    }
}
