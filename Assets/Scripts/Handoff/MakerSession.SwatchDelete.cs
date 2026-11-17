using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F81 (2026-11-14, the user: "a paint on the palette pinched for 2 seconds should start wobbling and show a delete
    // button, like deleting apps on an iPhone, so I can remove paints I don't use"). A pinch held on a swatch for
    // SwatchJiggleAfter without moving more than SwatchJiggleStill puts the palette in jiggle mode: every paint wobbles
    // and wears a small red x. A fingertip on an x (or a pinch on it, or an Editor click) deletes that paint (saved);
    // a pinch anywhere else on the palette, JiggleIdle without a delete, or the palette going back to Spark ends it.
    // F103 (Quest 2026-11-16, the user: "I asked for deleting paints on the palette and it isn't there - a long press or a
    // long pinch on a paint should bring up an X so I can delete it"): it was there, but only after a 2 s pinch held within
    // 2.5 cm of the blob's middle, and a fingertip resting on a paint only loaded the brush. Now a fingertip resting on a
    // paint (SwatchHoldPress) or a pinch held on it (SwatchJiggleAfter), each still within SwatchJiggleStill of where it
    // began, starts jiggle mode; while it waits the paint trembles more and more, so the person sees it coming. The brush
    // that touch loaded is put down again. An x is pressed only once the finger has left the paints (JiggleArmed).
    public sealed partial class MakerSession
    {
        public static float SwatchJiggleAfter = .8f, SwatchHoldPress = .8f, SwatchJiggleStill = .025f, JiggleIdle = 8f;
        public const float JiggleXRadius = .0075f, JiggleWaitShow = .2f;
        public bool Jiggling { get; private set; }
        public int SwatchesDeleted { get; private set; }
        public int JigglesByTouch { get; private set; }
        public int JigglesByPinch { get; private set; }
        private float jiggleUsedAt;
        private readonly List<Transform> jiggleXs = new List<Transform>();
        private readonly FingerPress jigglePress = new FingerPress();
        private bool jigglePinchWas, jiggleArmed;
        private Vector3 dragPinchStart;
        // The fingertip resting on a paint (F103).
        private Hand holdTipHand;
        private int holdTipSlot = -1;
        private float holdTipSince;
        private Vector3 holdTipStart;

        // UpdateSwatchDrag, while a swatch is pinched: held long and still -> jiggle mode (the drag is dropped).
        private bool SwatchHeldForJiggle()
        {
            if (dragHand == null || dragView == null || Jiggling) return false;
            if (Vector3.Distance(PinchPoint(dragHand), dragPinchStart) > SwatchJiggleStill) return false;
            float held = Time.time - dragStartAt;
            if (held < SwatchJiggleAfter)
            {
                if (!HandIO.Pinching(dragHand)) return false; // let go before: an ordinary drop (UpdateSwatchDrag)
                // (the dragged blob stays at the fingers and trembles more and more while it waits)
                float k = Mathf.Clamp01((held - JiggleWaitShow) / Mathf.Max(.05f, SwatchJiggleAfter - JiggleWaitShow));
                dragView.position = PinchPoint(dragHand);
                dragView.rotation = SpeechBubbleDock.FacingAt(dragView.position) * Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 30f) * 9f * k);
                return true;
            }
            dragSwatch = null;
            dragHand = null;
            mouseDragSwatch = false;
            if (dragView != null) Destroy(dragView.gameObject);
            dragView = null;
            JigglesByPinch++;
            StartJiggle();
            return true;
        }

        // UpdateDip, a fingertip in a paint (after it took the paint): resting there long and still -> jiggle mode.
        private void SwatchTipHold(Hand hand, int slot, Vector3 tip)
        {
            if (Jiggling) return;
            if (holdTipHand != hand || holdTipSlot != slot || Vector3.Distance(tip, holdTipStart) > SwatchJiggleStill)
            {
                holdTipHand = hand;
                holdTipSlot = slot;
                holdTipSince = Time.time;
                holdTipStart = tip;
                return;
            }
            float held = Time.time - holdTipSince;
            if (held < SwatchHoldPress)
            {
                float k = Mathf.Clamp01((held - JiggleWaitShow) / Mathf.Max(.05f, SwatchHoldPress - JiggleWaitShow));
                if (slot < swatchViews.Count && swatchViews[slot] != null)
                    swatchViews[slot].localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 30f) * 9f * k);
                return;
            }
            ForgetTipHold();
            PutBrushDown(); // (the touch that waited took the paint: a long press means "delete", not "paint")
            JigglesByTouch++;
            StartJiggle();
        }

        private void ForgetTipHold()
        {
            if (holdTipSlot >= 0 && holdTipSlot < swatchViews.Count && swatchViews[holdTipSlot] != null && !Jiggling)
                swatchViews[holdTipSlot].localRotation = Quaternion.identity;
            holdTipHand = null;
            holdTipSlot = -1;
        }

        public void StartJiggle()
        {
            if (Jiggling || swatchViews.Count == 0) return;
            Jiggling = true;
            jiggleUsedAt = Time.time;
            jigglePinchWas = true; // the pinch that started it is not an "elsewhere" pinch
            jiggleArmed = false;   // the finger that started it is still on the paints
            jigglePress.Clear();
            BuildJiggleXs();
            ToySounds.Play(ToySounds.Cue.Pop, paletteBoard != null ? paletteBoard.position : transform.position, .4f, .9f);
            WriteInputLog("palette: jiggle mode (delete paints)", true);
        }

        public void EndJiggle()
        {
            if (!Jiggling) return;
            Jiggling = false;
            foreach (var x in jiggleXs) if (x != null) Destroy(x.gameObject);
            jiggleXs.Clear();
            foreach (var v in swatchViews) if (v != null) v.localRotation = Quaternion.identity;
        }

        private void BuildJiggleXs()
        {
            foreach (var x in jiggleXs) if (x != null) Destroy(x.gameObject);
            jiggleXs.Clear();
            Color red = new Color(.9f, .26f, .24f), white = new Color(.99f, .98f, .97f);
            var disc = StickerMesh("jiggleX", () => ToyMeshes.Disc(JiggleXRadius, .0018f, red, Color.Lerp(red, Color.black, .25f), "JiggleX"));
            var bar = StickerMesh("jiggleBar", () => ToyMeshes.RoundedSlab(.0011f, .0085f, .0005f, .0005f, white, white, white, "JiggleBar"));
            foreach (var view in swatchViews)
            {
                var x = new GameObject("Delete Paint").transform;
                x.SetParent(view, false);
                x.localPosition = new Vector3(-BlobRadius * .8f, BlobRadius * .8f, -.006f);
                Part(x, "Disc", disc, Vector3.zero, 0f);
                for (int i = 0; i < 2; i++) Part(x, "Bar", bar, new Vector3(0f, 0f, -.0012f), i == 0 ? 45f : -45f);
                jiggleXs.Add(x);
            }
        }

        // ToolsFrame, every frame: the wobble, the x presses, the end.
        private void JiggleFrame()
        {
            if (!Jiggling) return;
            if (!PaletteOnPalm || paletteBoard == null || !paletteBoard.gameObject.activeInHierarchy || Time.time - jiggleUsedAt > JiggleIdle) { EndJiggle(); return; }
            for (int i = 0; i < swatchViews.Count; i++)
                if (swatchViews[i] != null) swatchViews[i].localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 22f + i * 1.7f) * 6f);
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) { jigglePress.Forget(hand); continue; }
                if (hand.Handedness == Handedness.Left) continue; // the palette's own hand
                bool tipKnown = HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip);
                // F103: the finger that started it (resting on a paint, by an x) first leaves the paints.
                if (!jiggleArmed)
                {
                    bool away = !tipKnown || (XNear(tip.position, .012f) < 0 && SwatchSlotNear(tip.position) < 0);
                    if (away && !HandIO.RawPinching(hand)) jiggleArmed = true;
                    jigglePinchWas = HandIO.Pinching(hand);
                    continue;
                }
                if (tipKnown && jigglePress.Step(this, hand, XNear(tip.position, .004f) >= 0))
                {
                    DeleteSwatch(XNear(tip.position, .004f));
                    return;
                }
                bool pinching = HandIO.Pinching(hand);
                if (pinching && !jigglePinchWas)
                {
                    Vector3 point = PinchPoint(hand);
                    int x = XNear(point, .01f);
                    if (x >= 0) { DeleteSwatch(x); jigglePinchWas = true; return; }
                    EndJiggle(); // a pinch elsewhere: done
                    jigglePinchWas = true;
                    return;
                }
                jigglePinchWas = pinching;
            }
        }

        private int XNear(Vector3 point, float margin)
        {
            for (int i = 0; i < jiggleXs.Count; i++)
                if (jiggleXs[i] != null && Vector3.Distance(jiggleXs[i].position, point) < JiggleXRadius + margin) return i;
            return -1;
        }

        // Editor: a click on an x.
        private bool JiggleClick(Ray ray)
        {
            if (!Jiggling) return false;
            for (int i = 0; i < jiggleXs.Count; i++)
                if (jiggleXs[i] != null && RayDistance(ray, jiggleXs[i].position) < JiggleXRadius + .003f) { DeleteSwatch(i); return true; }
            EndJiggle();
            return true;
        }

        public void DeleteSwatch(int shownIndex)
        {
            var shown = Palette.Skip(Mathf.Max(0, Palette.Count - PaletteSlots)).ToList();
            if (shownIndex < 0 || shownIndex >= shown.Count) return;
            var swatch = shown[shownIndex];
            Vector3 at = shownIndex < swatchViews.Count && swatchViews[shownIndex] != null ? swatchViews[shownIndex].position : transform.position;
            Palette.Remove(swatch);
            try { File.WriteAllText(PalettePath, JsonConvert.SerializeObject(new PaletteData { swatches = Palette }, Formatting.Indented)); }
            catch (Exception exception) { Debug.LogWarning("[Windups Palette] " + exception.Message); }
            SwatchesDeleted++;
            jiggleUsedAt = Time.time;
            ToyFx.Puff(at, .006f, 3);
            ToySounds.Play(ToySounds.Cue.Brush, at, .4f, 1.3f);
            Talk.Add("Person removed " + swatch.name + " from the palette");
            WriteInputLog("palette: deleted " + swatch.name + " " + swatch.hex, true);
            RebuildSwatches();
            if (swatchViews.Count == 0) { EndJiggle(); return; }
            BuildJiggleXs();
        }
    }
}
