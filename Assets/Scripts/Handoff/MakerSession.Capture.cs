using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Oculus.Interaction.Input;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // Picking from the room (2026-11-11). With the palette on the left palm, point the right hand at anything
    // and pinch: a small ring marks the spot while the pinch is held, and letting go drops the colour there
    // into the palette, however long the pinch was (after the 6th Quest try only a pinch let go within 0.4 s
    // counted, and most real pinches were longer). Moving the pinch more than CaptureDrift (a drag), the other
    // hand pinching too (the tape) or holding past CaptureLongest takes nothing. Textures come from the
    // two-hand L frame (the held pinch of the 5th try added textures by accident while grabbing): both hands
    // in an L, held still for FrameHold, frame what becomes the texture; when the frame looks onto a surface,
    // the material peels off that surface.
    // The texture's photo then goes to the image model, which keeps only the main material (the wood of
    // the desk, not the hand, the phone or the cup on it) as a seamless, evenly lit tile; the swatch (and
    // anything already painted with it) changes to it when it comes back. Spark works on it meanwhile.
    // Arriving: a colour pools where the finger points and pulls up into a drop (a liquid bloop), which
    // lands in its slot with a splat; a texture peels off the surface like a sticker and rolls up into a dab.
    // F49 (after the 15:00 Quest try, where 3 of 4 picks were dropped because the pinch moved 4 cm): the camera
    // picture is taken the moment the pinch starts and the spot is fixed then; moving the pinched hand no
    // longer cancels (only the other hand pinching, the tape, or a hold past CaptureLongest). The colour is read
    // next to the spot where no finger covers it. Every pick says how it went and writes it to the input log.
    // A texture framed on a surface is photographed once the hands are out of the way (the L only marks the
    // patch), so no hand is in it; framed in the air, only the middle of the frame is used.
    // F52: the colour pick's ring on the surface is gone; a drop at the fingertips fills with the colour while the
    // pinch is held and flies into the palette on release (MakerSession.PickDrop.cs).
    public sealed partial class MakerSession
    {
        public const float CaptureDrift = .04f, CaptureLongest = 10f, FrameHold = .7f;
        public string LastPick { get; private set; } = "";
        public int PicksFailed { get; private set; }

        // One colour pick: the picture taken at pinch time and the fingers then (kept out of the sample).
        private sealed class Pick
        {
            public RoomEyes.Frame frame;
            public bool done;
            public readonly List<Vector3[]> fingers = new List<Vector3[]>();
            // F52: read as soon as the picture is in (while the pinch is still held), shown in the drop.
            public bool sampled;
            public Color? colour;
            public Vector2 view;
        }
        private Pick pick;
        public int CaptureCancels { get; private set; }
        public bool AimShown => aimRing != null && aimRing.gameObject.activeSelf;
        private float frameSince = -1f, frameCooldown;
        public const string TexturePrompt =
            "Turn this photo into a seamless, tileable texture of its main surface material only (for example the wood of a desk, " +
            "the weave of a fabric, the stone of a counter, the paint of a wall). Remove hands, fingers, phones, cups, cables, papers " +
            "and every other object lying on it, and fill those areas with the same material. Show it straight from above, flat, with " +
            "even soft light: no shadows, no reflections, no glare, no perspective, no vignette, no edges or borders. Keep the real " +
            "colour, grain, scale and pattern of the material. Fill the whole square edge to edge.";

        public int TexturesCleaned { get; private set; }
        public bool CleaningTexture { get; private set; }
        public bool Capturing => captureHand != null;
        public int Peels { get; private set; }

        private Hand captureHand;
        private float captureSince;
        private Vector3 captureTarget, captureTip, captureWrist, captureNormal, captureStart;
        private bool captureOnSurface;
        private Transform aimRing;
        private float aimSize;

        // ------------------------------------------------------------ pinch and let go: colour

        private void BeginCapture(Hand hand)
        {
            if (!Aim(hand, out captureTarget, out captureTip, out captureWrist, out captureNormal, out captureOnSurface)) return;
            captureHand = hand;
            captureSince = Time.time;
            captureStart = PinchPoint(hand);
            // F52: no ring out on the surface (the room model can put it far from what the person sees): a drop
            // forms between the fingertips and fills with the colour (MakerSession.PickDrop.cs).
            StartDropper(captureStart);
            // F49: the picture now, while the spot is where the person sees it.
            var p = new Pick();
            FingerChains(hand, p.fingers);
            pick = p;
            if (Eyes != null) StartCoroutine(Eyes.Capture(f => { p.frame = f; p.done = true; }));
            else p.done = true;
        }

        // The pinching hand's fingers as chains of joints (palm to tip), to keep them out of the colour sample.
        private static readonly HandJointId[][] FingerJoints =
        {
            new[] { HandJointId.HandPalm, HandJointId.HandIndex1, HandJointId.HandIndex2, HandJointId.HandIndex3, HandJointId.HandIndexTip },
            new[] { HandJointId.HandPalm, HandJointId.HandThumb2, HandJointId.HandThumb3, HandJointId.HandThumbTip },
            new[] { HandJointId.HandPalm, HandJointId.HandMiddle1, HandJointId.HandMiddle2, HandJointId.HandMiddle3, HandJointId.HandMiddleTip }
        };

        private static void FingerChains(Hand hand, List<Vector3[]> into)
        {
            into.Clear();
            if (hand == null) return;
            foreach (var chain in FingerJoints)
            {
                var points = new List<Vector3>();
                foreach (var joint in chain) if (HandIO.Joint(hand, joint, out Pose pose)) points.Add(pose.position);
                if (points.Count >= 2) into.Add(points.ToArray());
            }
        }

        // Where to read the colour: the spot, or the nearest point beside it (further along the finger first)
        // that no finger covers in the camera picture.
        private static Vector2 PickPoint(RoomEyes.Frame frame, Vector3 target, Vector3 tip, Vector3 wrist, List<Vector3[]> fingers)
        {
            Vector2 targetView = frame.toViewport(target), tipView = frame.toViewport(tip), wristView = frame.toViewport(wrist);
            Vector2 beyond = (tipView - wristView).sqrMagnitude > 1e-6f ? (tipView - wristView).normalized : Vector2.up;
            var chains = (fingers ?? new List<Vector3[]>()).Select(c => c.Select(p => frame.toViewport(p)).ToArray()).ToList();
            bool Clear(Vector2 v)
            {
                if (v.x < .03f || v.x > .97f || v.y < .03f || v.y > .97f) return false;
                foreach (var chain in chains)
                    for (int i = 0; i + 1 < chain.Length; i++)
                    {
                        if (chain[i].x < -.5f || chain[i + 1].x < -.5f) continue; // behind the camera
                        if (SegmentDistance(v, chain[i], chain[i + 1]) < .035f) return false;
                    }
                return true;
            }
            foreach (float step in new[] { 0f, .02f, .04f, .06f, .08f })
                if (Clear(targetView + beyond * step)) return targetView + beyond * step;
            Vector2 side = new Vector2(-beyond.y, beyond.x);
            foreach (float s in new[] { .04f, -.04f, .07f, -.07f })
                if (Clear(targetView + side * s)) return targetView + side * s;
            return targetView + beyond * .025f;
        }

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (a + ab * t - p).magnitude;
        }

        // F49: how the last pick went, said once and written to the input log.
        private void PickResult(string result, bool ok)
        {
            LastPick = result;
            if (!ok) PicksFailed++;
            WriteInputLog("colour pick: " + result);
        }

        // The spot the colour comes from: a small ring lying on the surface (or facing the person in the air).
        private void ShowAim(Vector3 at, Vector3 normal)
        {
            if (aimRing == null)
            {
                var mesh = ToyMeshes.Torus(.14f, "Colour Aim");
                Paint(mesh, new Color(1f, .97f, .9f));
                owned.Add(mesh);
                aimRing = Part("Colour Aim", mesh);
            }
            float distance = Head != null ? Vector3.Distance(Head.position, at) : .6f;
            aimSize = Mathf.Clamp(distance * .016f, .007f, .03f);
            aimRing.SetPositionAndRotation(at + normal * .002f, Quaternion.FromToRotation(Vector3.forward, normal));
            aimRing.localScale = Vector3.one * aimSize;
            aimRing.gameObject.SetActive(true);
        }

        private void HideAim() { if (aimRing != null) aimRing.gameObject.SetActive(false); }

        // Where the finger points: from the eye through the fingertip, onto the room's surfaces.
        private bool Aim(Hand hand, out Vector3 target, out Vector3 tip, out Vector3 wrist, out Vector3 normal, out bool onSurface)
        {
            target = tip = wrist = normal = Vector3.zero;
            onSurface = false;
            if (Head == null || hand == null || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose t)) return false;
            tip = t.position;
            Vector3 dir = (tip - Head.position).normalized;
            var room = arrival.handoff.meeting.environment;
            if (Physics.Raycast(new Ray(tip, dir), out var hit, 3f, room.GeometryMask, QueryTriggerInteraction.Ignore))
            {
                target = hit.point;
                normal = hit.normal;
                onSurface = true;
            }
            else
            {
                target = tip + dir * .6f;
                normal = -dir;
            }
            wrist = HandIO.Joint(hand, HandJointId.HandWristRoot, out Pose w) ? w.position : tip - dir * .15f;
            return true;
        }

        private void UpdateCapture()
        {
            if (captureHand == null) return;
            float held = Time.time - captureSince;
            bool alive = captureHand.isActiveAndEnabled && HandIO.Connected(captureHand);
            var other = LiveHand(captureHand.Handedness == Handedness.Right ? Handedness.Left : Handedness.Right);
            // Not a pick: the hand is lost, the other hand pinches (the tape), or it was held a very long time.
            // (F49: moving the pinched hand no longer cancels: the spot and its picture were fixed at the pinch.)
            if (!alive || Drawing || held > CaptureLongest || (other != null && HandIO.Pinching(other)))
            {
                captureHand = null;
                pick = null;
                EndDropper(true);
                HideFramePreview();
                CaptureCancels++;
                ToyFx.Puff(captureTarget, .006f, 2);
                PickResult(!alive ? "cancelled: hand lost" : Drawing ? "cancelled: drawing" : held > CaptureLongest ? "cancelled: held too long" : "cancelled: other hand pinched", false);
                return;
            }
            if (HandIO.Pinching(captureHand) && PaletteOnPalm)
            {
                // F52: the colour shows in the drop as soon as the picture is in.
                if (pick != null && pick.done && !pick.sampled) LiveSample(pick);
                DropperFrame(PinchPoint(captureHand));
                return;
            }
            // Let go (or the palette went down meanwhile): the colour there, from the picture taken at the pinch;
            // the drop flies into the palette.
            captureHand = null;
            HideFramePreview();
            var taken = pick;
            pick = null;
            StartCoroutine(SampleColour(captureTarget, captureTip, captureWrist, null, taken, TakeDropper()));
        }

        // ------------------------------------------------------------ texture: frame it with both hands

        // Hold the frame still (FrameHold): the viewfinder closes in, then the surface inside it becomes a
        // seamless material tile (not the photo) in the palette.
        // F84 (2026-11-14, the user: "capturing a texture needs the palette open, which is hard in practice - let it work
        // without the palette"): making an L with the left hand turned the palette away, so the L frame works without
        // it; the texture flies to Spark's palette (MakerSession.HandOver.cs) and is there when the palette comes out.
        // F66 had it only while the palette was on the hand. Not while a hand works: measuring, drawing, sculpting,
        // the tape out, a loaded brush, a dragged swatch or Spark's frame carried.
        public static bool TextureWithoutPalette = true;
        public int TexturesToSpark { get; private set; }
        private bool TextureFrameAllowed()
        {
            if (!HandRules) return true;
            if (!TextureWithoutPalette) return PaletteOnPalm;
            return Current != Step.Measuring && !Drawing && !Sculpting && TapeAt == ToolAt.Crew && brushSwatch == null && dragSwatch == null && !FrameBusy;
        }

        // Spark holds its little palette out while a swatch flies into it (HandOverLate shows it until then).
        public const float SparkPaletteShow = 3.5f;
        private float sparkPaletteUntil = -10f;
        private bool SparkTakesSwatch()
        {
            if (!HandRules || PaletteOnPalm || PaletteAt != ToolAt.Crew || !CarryFree(Crew.Spark, false)) return false;
            sparkPaletteUntil = Time.time + SparkPaletteShow;
            Body(Crew.Spark)?.Play("HandRaise");
            return true;
        }

        // Where the swatch lands: Spark's palette once it is out, else where it will be (beside Spark's body).
        private Vector3 SparkSwatchPoint(Vector3 fallback)
        {
            if (sparkPalette != null && sparkPalette.gameObject.activeInHierarchy) return sparkPalette.position + Vector3.up * .006f;
            return cast.TryGetValue(Crew.Spark, out var spark) && spark != null ? spark.transform.position + Vector3.up * (CarryOutUp + .014f) : fallback;
        }

        private void UpdateTextureFrame()
        {
            if (Time.time < frameCooldown || Holding || Capturing || !TextureFrameAllowed()) { frameSince = -1f; HideFramePreview(); return; }
            if (!FrameRect(out var centre, out var facing, out float width, out float height)) { frameSince = -1f; HideFramePreview(); return; }
            if (frameSince < 0f) frameSince = Time.time;
            float progress = Mathf.Clamp01((Time.time - frameSince) / FrameHold);
            ShowFramePreview(centre, facing, width, height, progress);
            if (progress < 1f) return;
            frameSince = -1f;
            HideFramePreview();
            frameCooldown = Time.time + 2.5f;
            // Looking through the frame onto a surface: the material peels off there.
            Pose? onto = null;
            float size = 0f;
            if (Head != null)
            {
                Vector3 dir = (centre - Head.position).normalized;
                var room = arrival.handoff.meeting.environment;
                if (Physics.Raycast(new Ray(centre, dir), out var hit, 3f, room.GeometryMask, QueryTriggerInteraction.Ignore))
                {
                    PatchAt(hit.point, hit.normal, out var at, out var turn, out _);
                    float ratio = Vector3.Distance(Head.position, hit.point) / Mathf.Max(.1f, Vector3.Distance(Head.position, centre));
                    size = Mathf.Clamp(Mathf.Min(width, height) * ratio, .06f, .4f);
                    onto = new Pose(at, turn);
                }
            }
            // F49: on a surface the L only marks the patch; it is photographed once the hands are out of the way.
            // In the air only the middle of the frame is used (the fingers make its edges).
            if (onto.HasValue) StartCoroutine(TextureWhenHandsAway(centre, facing, width, height, onto.Value, size));
            else StartCoroutine(TakeTexture(centre, facing, width * .6f, height * .6f, null, null));
        }

        public bool WaitingForHands { get; private set; }        public float TextureHandWait { get; private set; }

        private IEnumerator TextureWhenHandsAway(Vector3 centre, Quaternion facing, float width, float height, Pose patch, float size)
        {
            WaitingForHands = true;
            frameCooldown = Time.time + 6f;
            // F98 (Quest 2026-11-15, the user: "a really huge circle appears when I extract a texture - remove that effect"):
            // the colour pick's aim ring was shown here at 55% of the patch (up to ~22 cm across). Now only the click.
            ToySounds.Play(ToySounds.Cue.Press, patch.position, .4f, 2.2f);
            float since = Time.time;
            if (HandsOverPatch(patch, size)) Caption(Crew.Spark, "Got the spot! Hands down and I'll snap it.", 2.5f);
            for (float until = Time.time + 3f; Time.time < until && HandsOverPatch(patch, size); ) yield return null;
            TextureHandWait = Time.time - since;
            WaitingForHands = false;
            frameCooldown = Time.time + 2.5f;
            WriteInputLog("texture: hands away after " + TextureHandWait.ToString("F1") + " s" + (HandsOverPatch(patch, size) ? " (still over it)" : ""));
            yield return TakeTexture(centre, facing, width, height, null, null, true, patch, size, PatchCorners(patch, size));
        }

        private static Vector3[] PatchCorners(Pose patch, float size)
        {
            Vector3 right = patch.rotation * Vector3.right * size * .5f, up = patch.rotation * Vector3.up * size * .5f;
            return new[] { patch.position - right - up, patch.position + right - up, patch.position - right + up, patch.position + right + up };
        }

        private static readonly HandJointId[] CoverJoints =
        {
            HandJointId.HandPalm, HandJointId.HandWristRoot, HandJointId.HandIndexTip, HandJointId.HandIndex2, HandJointId.HandThumbTip,
            HandJointId.HandThumb2, HandJointId.HandMiddleTip, HandJointId.HandMiddle1, HandJointId.HandRingTip, HandJointId.HandPinkyTip
        };

        // Some joint of a hand lies between the eyes and the patch (the camera would see the hand on it).
        private bool HandsOverPatch(Pose patch, float size)
        {
            if (Head == null) return false;
            var plane = new Plane(patch.rotation * Vector3.back, patch.position);
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) continue;
                foreach (var joint in CoverJoints)
                {
                    if (!HandIO.Joint(hand, joint, out Pose p)) continue;
                    Vector3 dir = p.position - Head.position;
                    var ray = new Ray(Head.position, dir.normalized);
                    if (!plane.Raycast(ray, out float d) || d < dir.magnitude) continue;
                    Vector3 local = Quaternion.Inverse(patch.rotation) * (ray.GetPoint(d) - patch.position);
                    if (Mathf.Abs(local.x) < size * .62f && Mathf.Abs(local.y) < size * .62f) return true;
                }
            }
            return false;
        }

        // Both hands in an "L" (thumb and index out, the other fingers curled). The frame is the rectangle
        // around both L's (corners and finger and thumb tips) as the person sees it: as wide and as tall as
        // the hands make it.
        private bool FrameRect(out Vector3 centre, out Quaternion facing, out float width, out float height)
        {
            centre = Vector3.zero;
            facing = Quaternion.identity;
            width = height = 0f;
            var left = LiveHand(Handedness.Left);
            var right = LiveHand(Handedness.Right);
            if (Head == null || left == null || right == null || !IsL(left, out var l0, out var l1, out var l2) || !IsL(right, out var r0, out var r1, out var r2)) return false;
            var points = new[] { l0, l1, l2, r0, r1, r2 };
            Vector3 middle = Vector3.zero;
            foreach (var point in points) middle += point;
            middle /= points.Length;
            Vector3 forward = (middle - Head.position).normalized;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, forward).normalized;
            Vector3 side = Vector3.Cross(up, forward);
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            foreach (var point in points)
            {
                float u = Vector3.Dot(point - middle, side), v = Vector3.Dot(point - middle, up);
                u0 = Mathf.Min(u0, u); u1 = Mathf.Max(u1, u); v0 = Mathf.Min(v0, v); v1 = Mathf.Max(v1, v);
            }
            width = u1 - u0;
            height = v1 - v0;
            centre = middle + side * ((u0 + u1) * .5f) + up * ((v0 + v1) * .5f);
            facing = Quaternion.LookRotation(forward, up);
            return width > .08f && width < .7f && height > .05f && height < .6f && Vector3.Distance(l0, r0) > .08f;
        }

        private static bool IsL(Hand hand, out Vector3 corner, out Vector3 indexTip, out Vector3 thumbTip)
        {
            corner = indexTip = thumbTip = Vector3.zero;
            if (HandIO.Pinching(hand)) return false;
            if (!HandIO.Joint(hand, HandJointId.HandPalm, out Pose palm) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose index) ||
                !HandIO.Joint(hand, HandJointId.HandThumbTip, out Pose thumb) || !HandIO.Joint(hand, HandJointId.HandMiddleTip, out Pose middle) ||
                !HandIO.Joint(hand, HandJointId.HandRingTip, out Pose ring) || !HandIO.Joint(hand, HandJointId.HandIndex1, out Pose indexBase)) return false;
            bool indexOut = Vector3.Distance(index.position, palm.position) > .08f;
            bool thumbOut = Vector3.Distance(thumb.position, index.position) > .06f;
            bool curled = Vector3.Distance(middle.position, palm.position) < .065f && Vector3.Distance(ring.position, palm.position) < .065f;
            corner = indexBase.position;
            indexTip = index.position;
            thumbTip = thumb.position;
            return indexOut && thumbOut && curled;
        }

        // The patch around a pointed spot: lying on the surface (or facing the person in the air), bigger the
        // further away it is.
        private void PatchAt(Vector3 target, Vector3 normal, out Vector3 centre, out Quaternion facing, out float size)
        {
            float distance = Head != null ? Vector3.Distance(Head.position, target) : .6f;
            size = Mathf.Clamp(distance * .24f, .1f, .4f);
            Vector3 view = Head != null ? (target - Head.position).normalized : Vector3.forward;
            Vector3 up = Vector3.ProjectOnPlane(view, normal);   // along the surface, away from the person
            if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(Vector3.up, normal);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.forward;
            facing = Quaternion.LookRotation(-normal, up.normalized);
            centre = target + normal * .004f;
        }

        // ------------------------------------------------------------ the image model cleans the texture

        private IEnumerator CleanTexture(Swatch swatch, byte[] photo)
        {
            if (Scripted || photo == null || swatch?.image == null || !WindupsDirectCredentials.TryGetKey(out string key)) yield break;
            CleaningTexture = true;
            working.Add(Crew.Spark);
            // 2026-11-15 (Quest: "after Spark explained the texture, there was a '...' for ages"): the "thinking" cue
            // turned Spark's bubble into dots for the whole picture edit (20-60 s) and hid the next lesson line behind
            // them. The cleaning goes on quietly; the dab's confetti and chime (ReplaceTile) say when it is done.
            byte[] result = null;
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("model", MakerRoles.ImageModel),
                new MultipartFormDataSection("prompt", TexturePrompt),
                new MultipartFormDataSection("size", "1024x1024"),
                new MultipartFormDataSection("quality", MakerRoles.ImageQuality),
                new MultipartFormDataSection("output_format", "jpeg"),
                new MultipartFormDataSection("output_compression", "90"),
                new MultipartFormFileSection("image[]", photo, "surface.jpg", "image/jpeg"),
            };
            using (var http = UnityWebRequest.Post("https://api.openai.com/v1/images/edits", form))
            {
                http.downloadHandler = new DownloadHandlerBuffer();
                http.SetRequestHeader("Authorization", "Bearer " + key);
                http.timeout = 120;
                yield return http.SendWebRequest();
                if (http.result == UnityWebRequest.Result.Success)
                    try { result = Convert.FromBase64String((string)JObject.Parse(http.downloadHandler.text)["data"][0]["b64_json"]); }
                    catch (Exception exception) { Debug.LogWarning("[Windups Palette] clean: " + exception.Message); }
                else Debug.LogWarning("[Windups Palette] clean failed: " + http.responseCode + " " + http.downloadHandler.text);
            }
            working.Remove(Crew.Spark);
            CleaningTexture = false;
            if (result == null) yield break;
            // F52: the cleaned tile keeps the photo's own colour and contrast (the image model drifts them).
            ReplaceTile(swatch, MaterialTile.Jpeg(result, 512, false, .3f, LoadSwatchTexture(swatch.image)) ?? result);
            TexturesCleaned++;
            Ack(Crew.Spark, "Cleaned it up: just the material now!");
        }

        // A swatch's tile changes (the cleaned texture): its file, its slot and anything painted with it.
        public void ReplaceTile(Swatch swatch, byte[] jpeg)
        {
            if (swatch?.image == null || jpeg == null) return;
            try { File.WriteAllBytes(swatch.image, jpeg); }
            catch (Exception exception) { Debug.LogWarning("[Windups Palette] " + exception.Message); return; }
            swatchTextures.Remove(swatch.image);
            var tile = LoadSwatchTexture(swatch.image);
            RebuildSwatches();
            foreach (var thing in PaintableThing.All)
            {
                if (thing == null) continue;
                foreach (var pair in new List<KeyValuePair<int, PaintableThing.Mark>>(thing.Marks))
                    if (pair.Value.image == swatch.image) thing.Apply(pair.Key, SwatchMaterial(pair.Value.kind, pair.Value.hex, pair.Value.image), pair.Value);
            }
            if (brushSwatch == swatch && brush != null) LoadBrush(swatch, brushHand, brushFinger);
            var view = ViewOf(swatch);
            if (view != null)
            {
                StartCoroutine(Grow(view, 1f, .3f));
                ToyFx.Confetti(view.position + Vector3.up * .01f, 5);
                ToySounds.Play(ToySounds.Cue.Chime, view.position, .3f, 1.9f);
            }
            if (tile != null) Note("palette", "Texture cleaned: " + swatch.name, "just the material", jpeg);
        }

        // ------------------------------------------------------------ arriving

        // The material peels off the surface like a sticker (hinged at its far edge, the near edge lifts toward
        // the person), then rolls up into a dab, which is returned for the flight to the palette.
        private IEnumerator PeelOff(Texture2D tile, Vector3 centre, Quaternion facing, float width, float height, Action<Transform> done)
        {
            Peels++;
            var hinge = new GameObject("Peel").transform;
            hinge.SetParent(transform, false);
            Vector3 along = facing * Vector3.up;
            hinge.SetPositionAndRotation(centre + along * height * .5f, facing);
            var sheet = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(sheet.GetComponent<Collider>());
            sheet.name = "Sheet";
            sheet.transform.SetParent(hinge, false);
            sheet.transform.localPosition = new Vector3(0f, -height * .5f, 0f);
            sheet.transform.localScale = new Vector3(width, height, .0015f);
            var r = sheet.GetComponent<MeshRenderer>();
            r.sharedMaterial = TileMaterial(tile, Vector2.one);
            r.shadowCastingMode = ShadowCastingMode.Off;
            ToySounds.Play(ToySounds.Cue.Peel, centre, .7f, 1f);
            for (float t = 0f; t < 1f && hinge != null; t += Time.deltaTime / .5f)
            {
                float e = Mathf.SmoothStep(0f, 1f, t);
                hinge.localRotation = facing * Quaternion.Euler(e * 150f, 0f, 0f);
                hinge.position = centre + along * height * .5f + (facing * Vector3.back) * e * .03f;
                sheet.transform.localScale = new Vector3(width * (1f - .25f * e), height * (1f - .45f * e), .0015f);
                yield return null;
            }
            // Rolls up into a dab of the material.
            Vector3 middle = sheet.transform.position;
            float size = Mathf.Clamp(Mathf.Min(width, height) * .5f, .04f, .08f);
            var dab = MaterialDab(tile, size * .5f);
            dab.position = middle;
            dab.rotation = SpeechBubbleDock.FacingAt(middle);
            for (float t = 0f; t < 1f && sheet != null; t += Time.deltaTime / .22f)
            {
                sheet.transform.localScale = Vector3.Lerp(sheet.transform.localScale, new Vector3(.002f, .002f, .0015f), t);
                dab.localScale = Vector3.one * size * .5f * Overshoot(t);
                yield return null;
            }
            Destroy(hinge.gameObject);
            ToySounds.Play(ToySounds.Cue.Pop, middle, .5f, 1.3f);
            done(dab);
        }

        // A colour pools on the surface where the finger points, then pulls up into a drop.
        private IEnumerator Puddle(Color colour, Vector3 at, Vector3 normal, Action<Transform> done)
        {
            var pool = ColourBall("Paint Pool", colour, .5f);
            pool.SetPositionAndRotation(at + normal * .001f, Quaternion.FromToRotation(Vector3.up, normal));
            pool.localScale = Vector3.zero;
            StartCoroutine(Ripple(at, colour, .8f));
            for (float t = 0f; t < 1f && pool != null; t += Time.deltaTime / .18f)
            {
                pool.localScale = new Vector3(.03f, .002f, .03f) * Overshoot(t);
                yield return null;
            }
            ToySounds.Play(ToySounds.Cue.Drip, at, .7f, 1f);
            var drop = ColourBall("Colour Drop", colour, .011f);
            drop.position = at;
            for (float t = 0f; t < 1f && drop != null; t += Time.deltaTime / .28f)
            {
                // The pool shrinks as the drop pulls up out of it on a stretched neck, then snaps round.
                float e = Mathf.SmoothStep(0f, 1f, t);
                pool.localScale = new Vector3(.03f, .002f, .03f) * (1f - e);
                float stretch = Mathf.Sin(Mathf.Clamp01(t * 1.25f) * Mathf.PI);
                drop.position = at + normal * Mathf.Lerp(.002f, .03f, e);
                drop.rotation = Quaternion.FromToRotation(Vector3.up, normal);
                drop.localScale = new Vector3(.022f * (1f - .35f * stretch), .022f * (1f + 1.1f * stretch), .022f * (1f - .35f * stretch)) * Mathf.Lerp(.4f, 1f, e);
                yield return null;
            }
            if (pool != null) Destroy(pool.gameObject);
            done(drop);
        }

        // A wet landing: the slot squashes and springs back, little dots of paint fly out around it.
        private IEnumerator Splat(Transform slot, Color colour)
        {
            if (slot == null) yield break;
            ToySounds.Play(ToySounds.Cue.Splat, slot.position, .55f, 1.2f);
            var dots = new List<(Transform t, Vector3 dir)>();
            for (int i = 0; i < 6; i++)
            {
                var dot = ColourBall("Splat", colour, .0025f);
                dot.position = slot.position;
                float a = (i / 6f + UnityEngine.Random.Range(-.05f, .05f)) * Mathf.PI * 2f;
                Vector3 dir = slot.rotation * new Vector3(Mathf.Cos(a), Mathf.Sin(a), -.3f);
                dots.Add((dot, dir.normalized));
            }
            Vector3 rest = slot.localScale;
            for (float t = 0f; t < 1f; t += Time.deltaTime / .35f)
            {
                if (slot != null)
                {
                    float squash = Mathf.Sin(t * Mathf.PI * 2f) * (1f - t) * .35f;
                    slot.localScale = new Vector3(rest.x * (1f + squash), rest.y * (1f + squash), rest.z * (1f - squash));
                }
                foreach (var (dot, dir) in dots)
                    if (dot != null)
                    {
                        dot.position += dir * Time.deltaTime * .09f * (1f - t);
                        dot.localScale = Vector3.one * .005f * (1f - t * t);
                    }
                yield return null;
            }
            if (slot != null) slot.localScale = rest;
            foreach (var (dot, _) in dots) if (dot != null) Destroy(dot.gameObject);
        }

        // The surface's normal at a point the person looks at (else facing the person).
        private Vector3 SurfaceNormal(Vector3 at)
        {
            if (Head == null) return Vector3.up;
            Vector3 dir = at - Head.position;
            var room = arrival.handoff.meeting.environment;
            if (Physics.Raycast(new Ray(Head.position, dir.normalized), out var hit, dir.magnitude + .05f, room.GeometryMask, QueryTriggerInteraction.Ignore) &&
                Vector3.Distance(hit.point, at) < .06f)
                return hit.normal;
            return -dir.normalized;
        }

        // ------------------------------------------------------------ tests

        // Tests: a pinch held `seconds` while the pinch point moved `moved` metres, then let go.
        public void SimulateCapture(Vector3 target, Vector3 normal, float seconds, bool onSurface = true, float moved = 0f)
        {
            captureTarget = target;
            captureNormal = normal;
            captureOnSurface = onSurface;
            captureTip = target - normal * -.05f;
            captureWrist = captureTip + normal * .15f;
            ShowAim(target, normal);
            HideAim();
            // F49: moving the pinch no longer matters; only a very long hold takes nothing.
            if (seconds <= CaptureLongest) { StartCoroutine(SampleColour(target, target, target, null)); return; }
            CaptureCancels++;
            ToyFx.Puff(target, .006f, 2);
        }

        // Tests: an L frame held at `centre`, looking onto the surface at `onto` (peels there).
        public void SimulateFramePeel(Vector3 centre, float width, float height, Vector3 onto, Vector3 normal)
        {
            var facing = Quaternion.LookRotation((centre - Head.position).normalized, Vector3.up);
            PatchAt(onto, normal, out var at, out var turn, out _);
            float ratio = Vector3.Distance(Head.position, onto) / Mathf.Max(.1f, Vector3.Distance(Head.position, centre));
            StartCoroutine(TakeTexture(centre, facing, width, height, null, null, true, new Pose(at, turn), Mathf.Clamp(Mathf.Min(width, height) * ratio, .06f, .4f)));
        }

        public void SimulateCleanTile(int paletteIndex, byte[] jpeg)
        {
            if (paletteIndex >= 0 && paletteIndex < Palette.Count) ReplaceTile(Palette[paletteIndex], MaterialTile.Jpeg(jpeg, 512, false, .3f) ?? jpeg);
        }
    }
}
