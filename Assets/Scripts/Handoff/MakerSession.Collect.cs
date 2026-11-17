using System;
using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F78 airplane mode, step 2 (2026-11-14, the user: "now I can only type on post-its and nothing else works - do all
    // the steps"): Spark's frame (F56/F73) and Maker's sketchbook (F71) work while collecting, when the session never
    // Begins (no internet). Only their own frame steps run (never RunFrameSteps/OnPinch: those wake the palette, the
    // tape, the mic and the made things), driven by WindupsCollect each frame (CollectingFrame / CollectingLate):
    //  - the frame: carried while pinched (FrameDragFrame), its red button by fingertip, pinch, ray or the Editor mouse;
    //    the picture (ShowPicture -> CollectShot) goes to PictureCollected instead of becoming a shape card; any picture
    //    is taken (inspiration can be a colour or a view: no "nothing seen" refusal);
    //  - the sketchbook: Maker holds it out (StartOfferingBook, placed by CollectingLate); taken, the page opens where it
    //    is let go; the check (TakeDrawing -> CollectDrawing) sends the drawing to DrawingCollected and the page flies
    //    back to Maker. Lines go straight on the cards (OfflineLine), never through the voice queue.
    public sealed partial class MakerSession
    {
        public bool Collecting { get; private set; }
        // (picture as JPEG, a texture the receiver owns, where it came out, its turn)
        public Action<byte[], Texture2D, Vector3, Quaternion> PictureCollected, DrawingCollected;
        // A hand WindupsCollect is using (carrying a note): the frame and the book leave its pinches alone.
        public Func<Hand, bool> CollectHandBusy;
        // WindupsCollect's things on a ray (RemoteGrab's cursor): the distance, or a negative number.
        public Func<Ray, float> CollectedOnRay;

        public bool CollectFrameOut => FrameShown;
        public bool CollectBookOut => OfferingBook || Drawing || SketchbookHeld || takingSketchbook || paperInFlight || sketchbookDragging;
        public bool CollectToolsBusy => FrameBusy || capturingShow || CollectBookOut && !OfferingBook;
        public Transform CollectFrame => showFrame;
        public Transform CollectPaper => paper;
        public int CollectPinchesTaken { get; private set; }

        // A hand the frame or the book is using now.
        public bool CollectUsesHand(Hand hand) =>
            hand != null && ((FrameBusy && (frameHand == hand || frameHand2 == hand)) ||
                             (sketchbookHand == hand && (SketchbookHeld || takingSketchbook || sketchbookDragging)) ||
                             (Drawing && pageHand == hand));

        public void StartCollecting()
        {
            PrepareOffline();
            Collecting = true;
        }

        // Spark puts its frame out at `at`, standing and facing the eyes (no room: it never lies down by itself).
        public void PutCollectFrameOut(Vector3 at)
        {
            if (!Collecting || (FrameShown && !frameFlying)) return;
            BuildShowFrame();
            FrameLying = false;
            EndFrameDrag(false);
            handInFrame = handWasInFrame = false;
            frameHintSaid = true;
            Vector3 away = Head != null ? at - Head.position : Vector3.forward;
            StartCoroutine(FlyFrame(new Pose(at, Quaternion.LookRotation(away.sqrMagnitude > 1e-6f ? away.normalized : Vector3.forward, Vector3.up)), true));
        }

        // Maker holds its sketchbook out (again).
        public void OfferCollectBook()
        {
            if (!Collecting || CollectBookOut) return;
            StartOfferingBook();
        }

        // Each frame (from WindupsCollect.Update) while collecting.
        public void CollectingFrame()
        {
            if (!Collecting || Begun) return;
            try { if (FrameShown) ShowFrameFrame(); } catch (Exception exception) { LogStepError("collect frame", exception); }
            try
            {
                SketchbookFrame();
                if (Drawing) UpdateDrawingHands(); else PutSketchFingersAway();
            }
            catch (Exception exception) { LogStepError("collect sketchbook", exception); }
            try { CollectPinches(); } catch (Exception exception) { LogStepError("collect pinches", exception); }
#if UNITY_EDITOR
            try { CollectMouse(); } catch (Exception exception) { LogStepError("collect mouse", exception); }
#endif
        }

        // From WindupsCollect.LateUpdate: the frame's scan line and preview, the held-out book in Maker's mittens.
        public void CollectingLate()
        {
            if (!Collecting || Begun) return;
            float blend = 1f - Mathf.Exp(-Time.deltaTime * 8f);
            try { if (FrameShown) ShowFrameLate(blend); } catch (Exception exception) { LogStepError("collect frame late", exception); }
            try
            {
                if (OfferingBook && !SketchbookHeld && !Drawing && !takingSketchbook && cast.TryGetValue(Crew.Maker, out var maker) && maker != null)
                    PlaceOfferedBook(maker, blend);
            }
            catch (Exception exception) { LogStepError("collect book late", exception); }
        }

        private readonly Dictionary<Hand, bool> collectPinching = new Dictionary<Hand, bool>();

        // A new pinch: the frame (rim, button, handle) or the book within reach first, then the page's buttons, then
        // what the hand's ray is on.
        private void CollectPinches()
        {
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) continue;
                bool raw = HandIO.RawPinching(hand);
                bool started = raw && !(collectPinching.TryGetValue(hand, out var was) && was);
                collectPinching[hand] = raw;
                if (!started || (CollectHandBusy != null && CollectHandBusy(hand))) continue;
                Vector3 point = PinchPoint(hand);
                Ray? ray = HandIO.Pointer(hand, out Pose pointer) ? new Ray(pointer.position, pointer.forward) : (Ray?)null;
                bool frameReady = FrameShown && !frameFlying;
                if (frameReady && ShowFramePinch(hand, point, null)) { CollectPinchesTaken++; continue; }
                if (SketchbookPinch(hand, point, null)) { CollectPinchesTaken++; continue; }
                if (Drawing && PageButtonPinch(ray)) { CollectPinchesTaken++; continue; }
                if (!ray.HasValue) continue;
                if (frameReady && ShowFramePinch(hand, Vector3.positiveInfinity, ray)) { CollectPinchesTaken++; continue; }
                if (SketchbookPinch(hand, Vector3.positiveInfinity, ray)) CollectPinchesTaken++;
            }
        }

#if UNITY_EDITOR
        // Editor preview: the mouse takes the frame (rim, button) and the book like a far pinch.
        private void CollectMouse()
        {
            if (UnityEngine.XR.XRSettings.isDeviceActive || HandIO.Active || Camera.main == null || !(UnityEngine.InputSystem.Mouse.current is { } mouse) || !mouse.leftButton.wasPressedThisFrame) return;
            Ray ray = Camera.main.ScreenPointToRay(mouse.position.ReadValue());
            if (FrameShown && !frameFlying && !FrameBusy && ShowFramePinch(null, Vector3.positiveInfinity, ray)) return;
            SketchbookPinch(null, Vector3.positiveInfinity, ray);
        }
#endif

        // RemoteGrab's cursor while collecting: the frame, the book or a collected thing on the hand's ray.
        private bool CollectCursor(Hand hand, out float distance, out bool ready)
        {
            distance = 0f;
            ready = true;
            if (hand == null || !HandIO.Pointer(hand, out Pose pointer) || CollectUsesHand(hand)) return false;
            if (CollectHandBusy != null && CollectHandBusy(hand)) return false;
            var ray = new Ray(pointer.position, pointer.forward);
            Vector3 point = PinchPoint(hand);
            float best = float.PositiveInfinity;
            if (FrameShown && !frameFlying && FrameOnRay(ray, out float d) && d < best) best = d;
            if ((OfferingBook || Drawing) && SketchbookOnRay(ray, out d) && d < best) best = d;
            if (CollectedOnRay != null) { d = CollectedOnRay(ray); if (d >= 0f && d < best) best = d; }
            if (float.IsInfinity(best)) return false;
            // (within reach the pinch itself takes it: no ray)
            if (!float.IsInfinity(point.x) && Vector3.Distance(point, ray.GetPoint(best)) <= RemoteGrab.NearLimit) return false;
            distance = best;
            return true;
        }

        // ShowPicture's end while collecting.
        private void CollectShot(RoomEyes.Frame frame, Shot shot, byte[] jpeg, Color32[] pixels, int width, int height)
        {
            Eyes?.UseDepth(false);
            if (frame == null)
            {
                ShowAnswer = "no picture";
                OfflineLine(Crew.Spark, "I can't see right now. Try again?");
                return;
            }
            if (shot == null || jpeg == null || pixels == null)
            {
                ShowAnswer = "frame not in view";
                OfflineLine(Crew.Spark, "Look at my frame with me?");
                PulseFrame();
                return;
            }
            ShowAnswer = "collected";
            ShowCaptures++;
            ScanDone(pixels, width, height);
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels);
            texture.Apply(false);
            Body(Crew.Spark)?.Play("Peek");
            Talk.Add("Spark caught a picture for the collection (" + shot.how + ").");
            PictureCollected?.Invoke(jpeg, texture, showFrame.position, showFrame.rotation);
        }

        // TakeDrawing while collecting: the page's drawing goes to DrawingCollected, the page flies back to Maker.
        private IEnumerator CollectDrawing(WindupCharacter maker)
        {
            paperInFlight = true;
            Drawing = false;
            penDown = false;
            drawingTimedOut = false;
            byte[] jpeg = RasterDrawing(drawn);
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp };
            if (!texture.LoadImage(jpeg)) { Destroy(texture); texture = null; }
            Talk.Add("The person drew a sketch for the collection.");
            if (texture != null) DrawingCollected?.Invoke(jpeg, texture, paper.position, paper.rotation);
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
            ClearPaper();
        }

        // ------------------------------------------------------------ back online: using what was collected

        public Material CardMaterial => material;
        // On (or just in front of) the current note, where an earlier idea or a picture is let go to use it (F72).
        public bool OnCurrentNote(Vector3 at) => Begun && NearCurrentNote(at);
        // A collected post-it let go on the current note: that idea, now (as an earlier idea from the wall).
        public void UseCollectedIdea(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            WriteInputLog("collected note on the current note: " + text, true);
            NewIdea(text);
        }
        // A collected picture or sketch let go on the current note: a reference for this step (as a wall picture).
        public void UseCollectedPicture(string path, string label, Transform view)
        {
            WriteInputLog("collected picture on the current note: " + label, true);
            UsePin(new Pin { image = path, label = label }, view);
        }

        // ------------------------------------------------------------ a collected post-it's Rewrite button

        // (2026-11-14, Quest: "there is no button to fix a post-it again") Each collected post-it carries the wall note's
        // Rewrite button (F72: the round button with the arrow, lower left). Pressed - a fingertip, a pinch at it, an
        // aimed pinch or the Editor mouse - WindupsCollect opens the keyboard on that post-it's words.
        private readonly Dictionary<Transform, RoundButton> collectButtons = new Dictionary<Transform, RoundButton>();
        private readonly List<Transform> collectButtonGone = new List<Transform>();

        // `owner` is the post-it's root; the button is placed at `local` (its face toward -Z, like the post-it's words).
        public Transform AddCollectRewrite(Transform owner, Vector3 local, float scale)
        {
            if (material == null || font == null) EnsureCrew();
            if (material == null || owner == null) return null;
            Color blue = MakerRoles.Color(Crew.Maker);
            var b = MakeRoundButton(owner, "Rewrite", local, NoteButtonRadius, Color.white, blue, "noteRewrite");
            b.spin = 70f;
            RewriteIcon(b, blue);
            b.label = RoundLabel(b, "Rewrite", Color.Lerp(blue, Color.black, .25f), true);
            b.baseScale = Vector3.one * scale;
            b.root.localScale = b.baseScale;
            collectButtons[owner] = b;
            return b.root;
        }

        public void ShowCollectRewrite(Transform owner, bool shown)
        {
            if (owner != null && collectButtons.TryGetValue(owner, out var b) && b.root != null && b.root.gameObject.activeSelf != shown)
            {
                b.root.gameObject.SetActive(shown);
                b.press.Clear();
            }
        }

        // Each frame: the hover look, and the post-it whose button a fingertip pressed (else null). `busy`: hands that
        // carry something press nothing.
        public Transform CollectButtonsFrame(Func<Hand, bool> busy)
        {
            Transform pressed = null;
            collectButtonGone.Clear();
            foreach (var pair in collectButtons)
            {
                if (pair.Key == null || pair.Value.root == null) { collectButtonGone.Add(pair.Key); continue; }
                pair.Value.near = float.MaxValue;
            }
            foreach (var key in collectButtonGone) collectButtons.Remove(key);
            foreach (var hand in HandSet.Both())
            {
                if (pressed != null || hand == null) break;
                bool free = busy == null || !busy(hand);
                if (!Fingertip(hand, out Vector3 tip)) { foreach (var b in collectButtons.Values) b.press.Forget(hand); continue; }
                foreach (var pair in collectButtons)
                {
                    if (!pair.Key.gameObject.activeInHierarchy) continue;
                    // (a busy hand's fingertip only updates the hover; RoundTouch would spend it)
                    if (!free) { pair.Value.near = Mathf.Min(pair.Value.near, Vector3.Distance(tip, pair.Value.root.position)); continue; }
                    if (RoundTouch(pair.Value, hand, tip)) { PressLog("collected note rewrite", hand); pressed = pair.Key; break; }
                }
            }
            foreach (var b in collectButtons.Values) RoundLook(b, false);
            if (pressed != null) RoundPressed(collectButtons[pressed]);
            return pressed;
        }

        // A pinch right at a button or the hand's ray on one: its post-it (and the distance along the ray), else null.
        public Transform CollectButtonHit(Vector3 point, Ray? ray, out float distance)
        {
            distance = 0f;
            RoundButton pick = null;
            float best = float.MaxValue;
            Transform owner = null;
            foreach (var pair in collectButtons)
            {
                if (pair.Key == null || !pair.Key.gameObject.activeInHierarchy) continue;
                var before = pick;
                ButtonHit(pair.Value, point, ray, ref best, ref pick);
                if (pick != before) owner = pair.Key;
            }
            if (owner != null) distance = best;
            return owner;
        }

        // The button was pressed by a pinch, a ray or the mouse: its look and click.
        public void CollectButtonPressed(Transform owner)
        {
            if (owner != null && collectButtons.TryGetValue(owner, out var b)) RoundPressed(b);
        }

        // Tests.
        public void SimulateCollectShot() => TakeShowPicture(true);
        public void SimulateTakeBook(Vector3 to)
        {
            if (!OfferingBook) return;
            paper.position = to;
            StartCoroutine(HandSketchbook(true));
        }
    }
}
