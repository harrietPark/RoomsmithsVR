using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F81 (2026-11-14, the user after the Quest try: "if I picked the wrong sketch and want to change the picture, how?").
    // After a pick the other sketches are kept aside (hidden) instead of being thrown away, and the chosen picture beside
    // Maker carries a raised "Change sketch" button under it. Pressed (fingertip, a pinch near or aimed from afar, Editor
    // click), or said ("other sketch", "wrong one", "change the picture", "다른 그림", "다시 고를래"), the measuring stops and
    // all the sketches come back in their row with Maker's pick prompt. This works until the making starts; then the kept
    // sketches go (the shape can still change with Change shape after the make).
    public sealed partial class MakerSession
    {
        public const float RePickW = .072f, RePickH = .022f;
        private static readonly Color RePickFace = new Color(.36f, .55f, .9f);
        private readonly List<Picture> setAsideLooks = new List<Picture>();
        private readonly List<Picture> pickedFrom = new List<Picture>();
        // F88 (Quest 2026-11-15: "Change sketch - they are already on the wall, and the unpicked ones are duplicated on the
        // wall"): the wall pictures a pick put up (the sketches not picked). Change sketch takes them down again, so the next
        // pick pins only its own others; once the making starts they stay on the wall for good.
        private readonly List<(Pin pin, Transform view)> rowPins = new List<(Pin, Transform)>();
        public int RowPinsTakenDown { get; private set; }
        private bool pickingAgain; // the row is back after Change sketch (Spark's "pinned" line is not said again)
        private Transform rePickButton;
        private readonly FingerPress rePickPress = new FingerPress();
        private bool rePickPressing;
        public int RePicks { get; private set; }
        public Transform RePickButton => rePickButton;
        public bool CanRePick => Look != null && setAsideLooks.Count > 0 && Current == Step.Measuring && Product == null && Sculpt == SculptStage.None;

        // PickLook: the others are kept aside; the chosen one gets the button.
        private void KeepOthersAside(Picture chosen)
        {
            ClearSetAside();
            pickedFrom.Clear();
            pickedFrom.AddRange(Looks);
            foreach (var other in Looks.Where(l => l != chosen).ToList())
            {
                if (other.root != null) other.root.gameObject.SetActive(false);
                setAsideLooks.Add(other);
                Looks.Remove(other);
            }
            BuildRePickButton(chosen);
        }

        private void BuildRePickButton(Picture chosen)
        {
            if (rePickButton != null) Destroy(rePickButton.gameObject);
            rePickButton = null;
            if (chosen?.root == null) return;
            Color role = Color.Lerp(RePickFace, Color.black, .2f);
            var pill = Panel("Change Sketch", RePickW, RePickH, RePickFace, out var text, 11f);
            text.text = "Change sketch";
            text.color = Color.white;
            text.fontStyle = FontStyles.Bold;
            MakeRaised(pill, RePickW, RePickH, RePickFace, role);
            pill.SetParent(chosen.root, false);
            pill.localPosition = new Vector3(0f, -chosen.size * .5f - .045f, -.006f);
            pill.localRotation = Quaternion.identity;
            rePickButton = pill;
            rePickPressing = false;
            rePickPress.Clear();
        }

        // PickLook: the pins it just put up (Pins and PinViews grow together, one pair per picture).
        private void RememberRowPins(int pinsBefore, int viewsBefore)
        {
            rowPins.Clear();
            for (int i = pinsBefore, v = viewsBefore; i < Pins.Count && v < PinViews.Count; i++, v++) rowPins.Add((Pins[i], PinViews[v]));
        }

        // RePick: the pictures this pick put on the wall come down (they are back in the row).
        private void TakeDownRowPins()
        {
            foreach (var (pin, view) in rowPins)
            {
                if (pinCarry != null && pinCarry.view == view) continue; // in the person's hand: it is theirs now
                if (pin == null || !Pins.Contains(pin)) continue;          // already gone (crumpled into the basket)
                if (view != null) ToyFx.Puff(view.position, .006f, 3);
                RemovePin(pin, view);
                RowPinsTakenDown++;
            }
            rowPins.Clear();
        }

        private void ClearSetAside()
        {
            foreach (var p in setAsideLooks) if (p?.root != null) Destroy(p.root.gameObject);
            setAsideLooks.Clear();
            pickedFrom.Clear();
            rowPins.Clear(); // the making started: the others stay on the wall
            if (rePickButton != null) Destroy(rePickButton.gameObject);
            rePickButton = null;
        }

        // Every frame (frame step "re-pick"): the button shows only while a re-pick is possible; a fingertip presses it.
        private void RePickFrame()
        {
            if (setAsideLooks.Count == 0 && rePickButton == null) return;
            // The making started (or the session moved on): the kept sketches go.
            if (Current != Step.Measuring && Current != Step.Looks) { ClearSetAside(); return; }
            if (rePickButton == null) return;
            bool show = CanRePick;
            if (rePickButton.gameObject.activeSelf != show) rePickButton.gameObject.SetActive(show);
            if (!show || rePickPressing) return;
            // (the button keeps its real size on the picture that was made smaller beside Maker)
            float s = rePickButton.parent != null ? rePickButton.parent.lossyScale.x : 1f;
            rePickButton.localScale = Vector3.one / Mathf.Max(.2f, s);
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip))
                {
                    rePickPress.Forget(hand);
                    continue;
                }
                if (rePickPress.Step(this, hand, OnRePick(tip.position, PressMargin, PressNear, PressThrough)))
                {
                    PressLog("change sketch", hand);
                    PressRePick();
                    return;
                }
            }
        }

        private bool OnRePick(Vector3 world, float margin, float back, float front)
        {
            if (rePickButton == null || !rePickButton.gameObject.activeInHierarchy || rePickPressing || float.IsInfinity(world.x)) return false;
            Vector3 l = rePickButton.InverseTransformPoint(world);
            return Mathf.Abs(l.x) <= RePickW * .5f + margin && Mathf.Abs(l.y) <= RePickH * .5f + margin && l.z >= back && l.z <= front;
        }

        private bool RePickOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            if (rePickButton == null || !rePickButton.gameObject.activeInHierarchy || rePickPressing) return false;
            if (!new Plane(rePickButton.forward, rePickButton.position).Raycast(ray, out float d) || d <= 0f || d > 4f) return false;
            if (!OnRePick(ray.GetPoint(d), .004f + d * .01f, -.01f, .01f)) return false;
            distance = d;
            return true;
        }

        // A pinch near the button, aimed at it from afar, or an Editor click.
        private bool RePickPinch(Vector3 point, Ray? ray)
        {
            if (!CanRePick || rePickPressing) return false;
            if (!OnRePick(point, .012f, -.03f, .03f) && !(ray.HasValue && RePickOnRay(ray.Value, out _))) return false;
            PressRePick();
            return true;
        }

        public void PressRePick()
        {
            if (!CanRePick || rePickPressing) return;
            rePickPressing = true;
            PressRaised(rePickButton);
            ToySounds.Play(ToySounds.Cue.Press, rePickButton.position, .5f, 1.6f);
            StartCoroutine(After(.25f, RePick));
        }

        private static readonly string[] RePickWords =
        {
            "other sketch", "another sketch", "different sketch", "other picture", "another picture", "different picture",
            "wrong one", "wrong picture", "wrong sketch", "change the picture", "change the sketch", "change sketch", "pick again", "choose again",
            "다른 그림", "다른 스케치", "다른 걸로", "다른거", "다른 거", "잘못 골", "다시 고를", "다시 골라", "그림 바꿀", "그림 바꿔", "스케치 바꿔"
        };

        // "the other one" and the like while measuring: the sketches again.
        private bool RePickVoice(string p)
        {
            if (!CanRePick || p == null) return false;
            if (!RePickWords.Any(p.Contains)) return false;
            RePick();
            return true;
        }

        // Back to the row of sketches; nothing measured is kept.
        public void RePick()
        {
            rePickPressing = false;
            if (!CanRePick) return;
            RePicks++;
            var chosen = Looks.FirstOrDefault();
            Hush();
            StopLessons();
            CloseQuestion("changing the sketch");
            ShowTape(false);
            ClearSizeBox();
            SizePlan.Clear();
            HasMeasure = false;
            Look = null;
            LookJpeg = null;
            if (lookJob == "forms") FormJpeg = null;
            Looks.Clear();
            foreach (var p in pickedFrom)
            {
                if (p?.root == null) continue;
                p.root.gameObject.SetActive(true);
                p.root.localScale = Vector3.one;
                Looks.Add(p);
            }
            setAsideLooks.Clear();
            pickedFrom.Clear();
            TakeDownRowPins(); // F88: no copies of the row left on the wall
            pickingAgain = true;
            if (rePickButton != null) Destroy(rePickButton.gameObject);
            rePickButton = null;
            rowFor = null; // the row is placed again
            Current = Step.Looks;
            Talk.Add("The person wants a different sketch.");
            Note("look", "Changed the pick", "Back to the sketches.");
            WriteInputLog("change sketch: back to " + Looks.Count + " sketches", true);
            ToySounds.Play(ToySounds.Cue.Whoosh, chosen?.root != null ? chosen.root.position : transform.position, .35f, 1.3f);
            AskWith(Crew.Maker, "Sure! Pick another sketch.", false, Rp(CompareLabel, CompareSay));
        }
    }
}
