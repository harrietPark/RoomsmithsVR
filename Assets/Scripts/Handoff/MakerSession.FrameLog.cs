using System.Text;
using UnityEngine;

namespace Windups.Handoff
{
    // 2026-11-15 (second Quest try: "is the fps okay overall? the passthrough feels shaky"): the headset's own FPS lines
    // in logcat only reach back ~25 s, so a test's frame drops were lost. Every FrameLogEvery seconds the session writes
    // one line to maker_input_log.txt: frames, average fps, the slowest frame, how many frames were over FrameBudgetMs,
    // the step and what was running then (a make, the parts being found, sculpting, Spark looking, a print...). The
    // counting is a few float adds a frame; the line is built only when written.
    public sealed partial class MakerSession
    {
        public static float FrameLogEvery = 10f, FrameBudgetMs = 14.5f; // (72 Hz: 13.9 ms a frame)
        public float FrameLogWorstMs { get; private set; }
        public int FrameLogLines { get; private set; }
        private float frameLogFrom = -1f, frameLogWorst, frameLogSum;
        private int frameLogFrames, frameLogOver;
        private string frameLogBusy = "";
        private readonly StringBuilder frameLogText = new StringBuilder(160);

        // From SolidTicker, every frame.
        private void FrameLogTick()
        {
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            if (frameLogFrom < 0f) { frameLogFrom = now; return; }
            frameLogFrames++;
            frameLogSum += dt;
            if (dt * 1000f > FrameBudgetMs) frameLogOver++;
            if (dt > frameLogWorst)
            {
                frameLogWorst = dt;
                frameLogBusy = FrameLogBusy(); // (what was going on at the slowest frame)
            }
            if (now - frameLogFrom < FrameLogEvery) return;
            FrameLogWorstMs = Mathf.Max(FrameLogWorstMs, frameLogWorst * 1000f);
            frameLogText.Clear();
            frameLogText.Append("frames | ").Append((frameLogFrames / Mathf.Max(.001f, frameLogSum)).ToString("0.0")).Append(" fps avg over ")
                .Append(frameLogFrames).Append(" frames, worst ").Append((frameLogWorst * 1000f).ToString("0")).Append(" ms, over ")
                .Append(FrameBudgetMs.ToString("0.#")).Append(" ms: ").Append(frameLogOver).Append(" | step ").Append(Current)
                .Append(frameLogBusy.Length > 0 ? " | at the worst: " + frameLogBusy : "");
            WriteInputLog(frameLogText.ToString());
            FrameLogLines++;
            frameLogFrom = now;
            frameLogFrames = frameLogOver = 0;
            frameLogSum = frameLogWorst = 0f;
            frameLogBusy = "";
        }

        // What the session is doing now, in a few words (only at a new slowest frame).
        private string FrameLogBusy()
        {
            var b = new StringBuilder(64);
            void Add(bool on, string what) { if (!on) return; if (b.Length > 0) b.Append(", "); b.Append(what); }
            Add(Modelling, "modelling");
            Add(Sculpt != SculptStage.None, "carving " + Sculpt);
            Add(PartsBusy, "parts");
            Add(Sculpting, "sculpting");
            Add(Eyes != null && Eyes.Looking, "looking");
            Add(Printing, "printing");
            Add(CleaningTexture, "texture clean");
            Add(Capturing, "colour pick");
            Add(splitsRunning > 0, "part split");
            Add(Drawing, "drawing");
            Add(Comparing, "comparing");
            return b.ToString();
        }
    }
}
