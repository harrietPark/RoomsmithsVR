using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // (2026-11-12, after a Quest try where the palette, the texture frame and the tape all did nothing and the
    // headset kept no app log.) The per-frame parts of the session run one by one, each on its own: one that
    // throws is written down and the others go on. persistentDataPath/maker_input_log.txt keeps, once a
    // second while a hand is tracked, what the hands do and what each tool thinks (palm up, palette, the
    // tape and why it is off, the texture frame, a colour pick), who took each pinch, and the errors.
    public sealed partial class MakerSession
    {
        public static string InputLogPath => Path.Combine(Application.persistentDataPath, "maker_input_log.txt");
        public readonly List<string> StepErrors = new List<string>();
        public string LastPinchBy => lastPinchBy;
        public bool PaletteShown => paletteBoard != null && paletteBoard.gameObject.activeInHierarchy;
        public Transform NotebookClosedView => notebookClosed;

        private string lastPinchBy = "";
        private (string name, Action step)[] frameSteps;
        private readonly Dictionary<string, int> stepErrorCounts = new Dictionary<string, int>();
        private float inputLogAt;
        private readonly StringBuilder inputLog = new StringBuilder();

        private void RunFrameSteps()
        {
            frameSteps ??= new (string, Action)[]
            {
                ("hand gate", HandGateFrame), ("notebook grab", UpdateNotebookGrab), ("sketchbook grab", SketchbookFrame), ("tape", UpdateTape), ("hand over", HandOverFrame), ("tape pin", TapePinFrame), ("print marks", PrintMarksFrame),
                ("tape settle", SettleFrame), ("size box", SizeBoxFrame),("tape coach", TapeCoachFrame), ("return", ReturnFrame), ("work space", WorkSpaceFrame), ("clearance", ClearanceFrame), ("note carry", NoteCarryFrame), ("wall items", WallItemsFrame),
                ("tools", ToolsFrame), ("inputs", InputsFrame), ("show frame", ShowFrameFrame), ("shape cards", ShapeCardsFrame), ("trace finger", TraceFingerFrame), ("lessons", LessonFrame), // F49
                ("colour done", ColourDoneAskFrame), // F95
                ("make another", MakeAnotherFrame) // F98
            };
            foreach (var (name, step) in frameSteps)
            {
                try { step(); }
                catch (Exception exception) { LogStepError(name, exception); }
            }
        }

        private void LogStepError(string name, Exception exception)
        {
            stepErrorCounts.TryGetValue(name, out int count);
            stepErrorCounts[name] = ++count;
            if (count > 3) return; // the first few of each (a broken part would fill the log every frame)
            string line = name + ": " + exception.GetType().Name + " " + exception.Message + " | " + (exception.StackTrace ?? "").Replace("\n", " / ");
            StepErrors.Add(line);
            Debug.LogError("[Windups Input] " + line);
            WriteInputLog("ERROR " + line);
        }

        private void LogPinch(Hand hand, Vector3 point)
        {
            // F73: with Spark's frame out, how far the pinch was from it (a missed grab shows what took the pinch instead).
            string frame = "";
            if (FrameShown && !frameFlying && !float.IsInfinity(point.x))
            {
                float d = FrameDistance(point, out _);
                if (d < .15f) frame = " | frame " + (d * 100f).ToString("0.0") + " cm" + (FrameBusy ? " (in use)" : "");
            }
            // F102: a pinch on nothing says why the far ray did not press (and which button its ray passed nearest).
            string miss = lastPinchBy == "nothing" ? FarMissNote(hand) : "";
            WriteInputLog("pinch " + (hand != null ? hand.Handedness.ToString() : "?") + " at " + point.ToString("F2") + " -> " + lastPinchBy + frame + miss);
        }

        private string farGateNote = "";
        private string FarMissNote(Hand hand)
        {
            string note = string.IsNullOrEmpty(farGateNote) ? "" : " | " + farGateNote;
            if (hand == null || !HandIO.Pointer(hand, out Pose own, false)) return note;
            float best = 12f;
            string name = null;
            foreach (var r in raisedPills)
            {
                if (r.root == null || !r.root.gameObject.activeInHierarchy) continue;
                float a = Vector3.Angle(own.forward, r.root.position - own.position);
                if (a < best) { best = a; name = r.root.name; }
            }
            return name != null ? note + " | ray " + best.ToString("0.0") + " deg off button '" + name + "'" : note;
        }

        private void InputLogFrame()
        {
            if (Time.time < inputLogAt) return;
            var left = HandSet.Side(Handedness.Left);
            var right = HandSet.Side(Handedness.Right);
            bool l = left != null && left.isActiveAndEnabled && HandIO.Connected(left), r = right != null && right.isActiveAndEnabled && HandIO.Connected(right);
            if (!l && !r) return;
            inputLogAt = Time.time + 1f;
            string palm = "no";
            if (l && HandIO.TryPalm(left, out var centre, out var normal) && Head != null)
                palm = "up " + Vector3.Dot(normal, Vector3.up).ToString("F2") + " eye " + Vector3.Dot(normal, (Head.position - centre).normalized).ToString("F2") +
                       " view " + PalmViewAngle(centre).ToString("0") + " deg"; // F102
            var sb = new StringBuilder();
            sb.Append("step ").Append(Current)
              .Append(" | L ").Append(l ? (HandIO.Pinching(left) ? "pinch" : "open") : "-").Append(" palm ").Append(palm)
              .Append(" | R ").Append(r ? (HandIO.Pinching(right) ? "pinch" : "open") : "-")
              .Append(" | palette ").Append(HandRules ? PaletteAt.ToString() : PaletteOnPalm ? "on" : "off")
              .Append(" | tape ").Append(HandRules ? TapeAt + " " : "").Append(Tape.State).Append(Current == Step.Measuring ? " (measuring)" : FreeTapeAllowed ? " free" : " off: " + FreeTapeOff())
              .Append(" | frame ").Append(frameSince >= 0f ? "held" : "-")
              .Append(" | colour ").Append(Capturing ? "aiming" : "-")
              .Append(" | notebook ").Append(NotebookOpen ? (NotebookHeld ? "held" : "open") : "closed");
            WriteInputLog(sb.ToString());
        }

        // Why the anytime tape is off right now (for the log).
        private string FreeTapeOff()
        {
            var why = new List<string>();
            if (!Begun) why.Add("not begun");
            if (Current == Step.Making) why.Add("making");
            if (Holding) why.Add("holding");
            if (Dragging != null) why.Add("dragging a swatch");
            if (Drawing) why.Add("drawing");
            if (Capturing) why.Add("picking a colour");
            if (tapeDemo) why.Add("lesson");
            return string.Join(", ", why);
        }

        // `now` (F60): written to the file at once (presses, grabs, let-gos: the Quest log lost the last seconds before
        // the stand vanished). Otherwise every 2 s or 2000 characters.
        private float inputLogFlushAt;
        private void WriteInputLog(string line, bool now = false)
        {
            inputLog.Append(DateTime.Now.ToString("HH:mm:ss.f ")).Append(line).Append('\n');
            if (!now && inputLog.Length < 2000 && Time.unscaledTime < inputLogFlushAt && !line.StartsWith("ERROR", StringComparison.Ordinal)) return;
            inputLogFlushAt = Time.unscaledTime + 2f;
            try
            {
                var file = new FileInfo(InputLogPath);
                if (file.Exists && file.Length > 400_000) file.Delete(); // keep it small: the latest session matters
                File.AppendAllText(InputLogPath, inputLog.ToString());
            }
            catch { }
            inputLog.Clear();
        }

        private void OnApplicationPause(bool paused) { if (paused && inputLog.Length > 0) WriteInputLog("(paused)"); }
    }
}
