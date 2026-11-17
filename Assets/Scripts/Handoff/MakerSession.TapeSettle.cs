using UnityEngine;

namespace Windups.Handoff
{
    // The tape needs no "okay" (2026-11-11). A length taken with the hands at the measuring step is used after
    // a short grace (SettleSeconds; a measurement from earlier, offered by Fixer, OfferedSettleSeconds) while a
    // small bar fills under the tape's number. Pinching with both hands again restarts it (the new length starts
    // its own grace), "again" cancels it, and talking pauses it. "Okay", a thumbs up or Enter still go on at once,
    // and a spoken size ("85 millimetres") or "use yours" goes on at once too. Trays keep their own confirm
    // (their traced outlines can be fixed by hand first). After moving on, the tape anytime + "use it" refits.
    public sealed partial class MakerSession
    {
        public const float SettleSeconds = 3f, OfferedSettleSeconds = 4f;
        // A plain "okay" this soon after the measure went on by itself is the person confirming it, not a shape.
        public const float LateOkaySeconds = 10f;

        public bool Settling => settleAt >= 0f;
        public float SettleProgress => Settling ? Mathf.Clamp01((Time.time - settleAt) / settleFor) : 0f;
        public int AutoConfirms { get; private set; }
        public bool SettleBarShown => settleBar != null && settleBar.gameObject.activeInHierarchy;

        private float settleAt = -1f, settleFor = SettleSeconds, measureConfirmedAt = -1000f;
        private Transform settleBar, settleTrack;
        private const float SettleWidth = .064f;

        private void StartSettle(float seconds)
        {
            settleAt = Time.time;
            settleFor = Mathf.Max(.1f, seconds);
            ShowSettle(0f);
        }

        private void StopSettle()
        {
            settleAt = -1f;
            if (settleBar != null) settleBar.gameObject.SetActive(false);
            if (settleTrack != null) settleTrack.gameObject.SetActive(false);
        }

        private void SettleFrame()
        {
            if (settleAt < 0f) return;
            if (Current != Step.Measuring || !HasMeasure || Kind == "tray") { StopSettle(); return; }
            // Pinching again (a new length is coming) or talking (maybe "again" or a number) holds the grace.
            bool talking = arrival != null && arrival.microphone != null && (arrival.microphone.IsHolding || arrival.microphone.IsVoiceBusy);
            if (Tape.State == HandTape.Mode.TwoHands || talking)
            {
                settleAt = Time.time;
                ShowSettle(0f);
                return;
            }
            float t = (Time.time - settleAt) / settleFor;
            ShowSettle(t);
            if (t < 1f) return;
            StopSettle();
            AutoConfirms++;
            ConfirmMeasure();
        }

        private void ShowSettle(float t)
        {
            if (settleBar == null || tapeLabelCard == null) return;
            bool on = tapeLabelCard.gameObject.activeInHierarchy && Settling;
            if (settleTrack != null) settleTrack.gameObject.SetActive(on);
            settleBar.gameObject.SetActive(on);
            if (!on) return;
            const float full = SettleWidth;
            float w = Mathf.Max(.001f, Mathf.Clamp01(t) * full);
            settleBar.localPosition = new Vector3(-full * .5f + w * .5f, -.0085f, -.0026f);
            settleBar.localScale = new Vector3(w, 1f, 1f);
        }

        private void BuildSettleBar()
        {
            // Same white pill, font and blueprint blue as the existing dimension line.
            var trackMesh = Own(ToyMeshes.RoundedSlab(SettleWidth, .0018f, .0008f, .0007f,
                new Color(.82f, .86f, .92f), Blueprint, new Color(.82f, .86f, .92f), "TapeSettleTrack"));
            settleTrack = Part("Tape Timer Track", trackMesh);
            settleTrack.SetParent(tapeLabelCard, false);
            settleTrack.localPosition = new Vector3(0f, -.0085f, -.0018f);
            settleTrack.gameObject.SetActive(false);
            var fill = Own(ToyMeshes.RoundedSlab(1f, .0018f, .0008f, .0007f, Blueprint, Blueprint, Blueprint, "TapeSettle"));
            settleBar = Part("Tape Settle", fill);
            settleBar.SetParent(tapeLabelCard, false);
            settleBar.gameObject.SetActive(false);
        }

        // In the shape step, a bare "okay" right after the measure went on by itself only confirms it.
        private bool LateOkay(string p)
        {
            if (!IsYes(p) || Time.time - measureConfirmedAt > LateOkaySeconds) return false;
            Ack(Crew.Fixer, "Already got it!");
            return true;
        }
    }
}
