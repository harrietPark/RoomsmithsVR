using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Windups.Handoff
{
    // F45 (2026-11-12): after what to make, when the words name the thing it is for ("a mug coaster" -> mug,
    // "a stand for my phone" -> phone), Fixer asks for it: "Do you have the mug you'll use? If so, put it here!"
    // and Spark's frame comes onto the desk ("Your mug here"; F56: the frame replaced the show-me circle, and
    // "circle" below means the frame). Its button pressed -> Spark takes one picture with depth (ShowPicture in
    // MakerSession.ShowMe.cs): what stands in the lying frame is that thing (held in the air: picture only). Its
    // width (across the person's view) and height come from the depth points standing in the circle, its picture
    // is the crop; it becomes the Target with a name tag (the model then stands beside it, PlaceModelSpot, and
    // its width is Spark's hint when measuring). "No" / "I don't have one" / "skip" goes on without it; one
    // reminder after InputRemindAfter seconds. Nothing here looks around the room: only the circle is looked at.
    // The same circle is then the shape's "Show me" circle (never a second one). While the thing is still in it,
    // a look that finds the same thing there is not taken as the shape ("this" still takes it), and a look that
    // finds the circle emptied only says so.
    public sealed partial class MakerSession
    {
        public string ForObjectName { get; private set; } = "";
        public int ForObjectTaken { get; private set; }
        public bool ForObjectInCircle { get; private set; }    // the thing taken still stands in the circle
        public float ForObjectWidth { get; private set; }      // m, from depth (0: not measured)
        public float ForObjectHeight { get; private set; }
        public string ForObjectAnswer { get; private set; } = "";
        public int ForObjectReminders { get; private set; }

        private bool forObjectReminded;
        private float forObjectAskedAt;

        private static readonly string[] ForWords =
        {
            "coaster", "holder", "stand", "tray", "mat", "lid", "cover", "case", "dock", "rack", "organiser", "organizer", "hook", "hanger",
            "sleeve", "base", "shelf", "pad", "rest", "cozy", "cosy", "caddy", "plate", "dish", "box", "pot", "planter", "rack"
        };
        private static readonly string[] ForWordsKo = { "받침", "거치대", "홀더", "정리", "커버", "케이스", "스탠드", "트레이", "덮개", "코스터", "걸이", "선반" };

        // Is `noun` the thing the make is for ("a mug coaster", "a stand for my phone"), not the make itself ("a mug")?
        public static bool IsForThing(string need, string noun)
        {
            if (string.IsNullOrWhiteSpace(need) || string.IsNullOrEmpty(noun)) return false;
            string p = need.ToLowerInvariant();
            string n = Regex.Escape(noun);
            if (Regex.IsMatch(p, @"\b(for|fits?|holds?|under|around)\b.*\b" + n + @"s?\b")) return true;
            string rest = Regex.Replace(p, @"\b" + n + @"s?\b", " ");
            if (ForWords.Any(w => Regex.IsMatch(rest, @"\b" + w + @"s?\b"))) return true;
            return p.Any(c => c >= 0xAC00 && c <= 0xD7A3) && ForWordsKo.Any(p.Contains);
        }

        // After "what to make" (said, picked or on the note): its thing in the circle first, else the shape.
        private void AfterNeed()
        {
            string noun = Target == null && Kind != "clip" ? ObjectNoun(Need) : null;
            if (noun != null && IsForThing(Need, noun)) StartForObject(noun);
            else StartStyle();
        }

        private void StartForObject(string noun)
        {
            Current = Step.ForObject;
            ForObjectName = noun;
            ForObjectAnswer = "";
            ForObjectInCircle = false;
            ForObjectWidth = ForObjectHeight = 0f;
            forObjectReminded = false;
            forObjectAskedAt = Time.time;
            ResetInputTimers();
            if (!FrameShown) PlaceShowFrame();
            Talk.Add("Fixer asks for the person's " + noun + " in Spark's frame (what we make it for).");
            AskWith(Crew.Fixer, "Do you have the " + noun + " you'll use? Put it in Spark's frame and press the button!", false, Rp("I don't have one", "no"));
            StartCoroutine(ShowWith(() => { PulseFrame(); LookAt(Crew.Fixer, ShowCircleCentre, 2.5f); Body(Crew.Fixer)?.Play("HandRaise"); }));
        }

        // The circle step is over (taken, skipped, or found where the finger pointed): on to the shape.
        private void LeaveForObject()
        {
            if (Current != Step.ForObject) return;
            StartStyle();
        }

        private void SkipForObject(string why)
        {
            ForObjectAnswer = why;
            Talk.Add("The person has no " + ForObjectName + " here (" + why + ").");
            Ack(Crew.Fixer, "No problem!");
            LeaveForObject();
        }

        private void ResetForObject()
        {
            ForObjectName = ForObjectAnswer = "";
            ForObjectInCircle = false;
            ForObjectWidth = ForObjectHeight = 0f;
            forObjectReminded = false;
        }

        private static bool NoThingWords(string p) =>
            p == "no" || p == "nope" || p == "nah" || p.StartsWith("no,") || p.StartsWith("no ") || p.Contains("don't have") || p.Contains("dont have") ||
            p.Contains("do not have") || p.Contains("haven't got") || p.Contains("not here") || p.Contains("skip") || p.Contains("not now") ||
            p.Contains("없어") || p.Contains("없는데") || p.Contains("없음") || p.StartsWith("아니") || p.Contains("넘어가") || p.Contains("건너");

        // What is said while the thing is asked for.
        private bool ForObjectVoice(string text, string p)
        {
            if (NoThingWords(p)) { SkipForObject("said no"); return true; }
            if (ThisWords(p) || SeeWords(p) || DoneWords(p) || p.Contains("put it") || p.Contains("it's there") || p.Contains("넣었") || p.Contains("놨") || p.Contains("놓았"))
            {
                TakeShowPicture(true);
                return true;
            }
            if (IsYes(p) || p.Contains("i have") || p.Contains("got one") || p.Contains("있어"))
            {
                Ack(Crew.Fixer, "Great! Put it in the frame and press the button.");
                PulseFrame();
                return true;
            }
            Ack(Crew.Fixer, "Put your " + ForObjectName + " in the frame and press its button, or say no.");
            PulseFrame();
            return true;
        }

        // Every frame while it is asked for (InputsFrame): one reminder.
        private void ForObjectFrame()
        {
            if (!FrameShown && !frameFlying) PlaceShowFrame();
            bool busy = capturingShow || (Eyes != null && Eyes.Looking) || WindupBody.UserTalking || lines.Count > 0 || Voice.IsSpeaking || Voice.Loading;
            if (forObjectReminded || busy || Time.time - Mathf.Max(forObjectAskedAt, Mathf.Max(lastInputAt, lastHeardAt)) < InputRemindAfter) return;
            forObjectReminded = true;
            ForObjectReminders++;
            Say(Crew.Fixer, "Put your " + ForObjectName + " in the frame and press its button, or say no if it's not here.");
            PulseFrameButton();
        }

        private string CircleIdleLabel => Current == Step.ForObject ? (ForObjectName.Length <= 6 ? "Your " + ForObjectName + " here" : "Put it here") : "Show me";

        // How wide the standing points are across the person's view (what the eye sees as its width), in metres.
        private float WidthAcrossView(List<Vector3> standing)
        {
            if (standing == null || standing.Count < 3) return 0f;
            Vector3 c = standing.Aggregate(Vector3.zero, (a, b) => a + b) / standing.Count;
            Vector3 view = Head != null ? Vector3.ProjectOnPlane(c - Head.position, Vector3.up) : Vector3.forward;
            if (view.sqrMagnitude < 1e-6f) view = Vector3.forward;
            Vector3 across = Vector3.Cross(Vector3.up, view.normalized);
            var xs = standing.Select(q => Vector3.Dot(q - c, across)).OrderBy(x => x).ToList();
            // The outermost points lie up to one depth-grid step inside the real edges (a mug measured 66 mm for 82):
            // add the typical step between neighbouring points.
            var steps = new List<float>();
            for (int i = 0; i < standing.Count; i++)
            {
                float nearest = float.PositiveInfinity;
                for (int j = 0; j < standing.Count; j++)
                    if (i != j) nearest = Mathf.Min(nearest, Vector3.Distance(standing[i], standing[j]));
                if (nearest > .001f && nearest < .05f) steps.Add(nearest);
            }
            steps.Sort();
            float step = steps.Count > 0 ? steps[steps.Count / 2] : 0f;
            return xs[xs.Count - 1] - xs[0] + step;
        }

        // ShowPicture during the circle step: the thing it is for (or not there yet).
        private void ForObjectSeen(bool present, bool asked, byte[] crop, List<Vector3> standing, float level, float top, bool lying = true)
        {
            string name = ForObjectName;
            if (!present && !asked)
            {
                ShowAnswer = "nothing seen";
                nothingSeenAt = Time.time;
                ScanFailed("Hmm, I don't see your " + name + " in my frame yet. Put it in and press again, or say no.");
                PulseFrame();
                return;
            }
            // F56: the frame held up in the air: its picture and name, but no place on a surface and no size (the
            // tape measures it later).
            if (!lying) { standing = new List<Vector3>(); present = false; }
            float width = present ? WidthAcrossView(standing) : 0f;
            float height = present && standing.Count >= 3 ? top : 0f;
            Vector3 middle = standing.Count >= 3 ? standing.Aggregate(Vector3.zero, (a, b) => a + b) / standing.Count : ShowCircleCentre;
            var foot = new Vector3(middle.x, level, middle.z);
            var found = new RoomEyes.Found
            {
                seen = new RoomEyes.Seen { name = name, focus = true, colors = new string[0], surface = "" },
                basePoint = foot,
                top = foot + Vector3.up * Mathf.Max(height, .02f),
                width = width,
                height = height,
                onSurface = lying,
                byDepth = standing.Count >= 3,
                placed = lying,
                crop = crop,
                note = lying ? "put in Spark's frame" : "shown through Spark's frame in the air"
            };
            Target = found;
            TargetName = name;
            if (lying) ShowTargetTag(found, name, false);
            ForObjectInCircle = present;
            ForObjectWidth = width;
            ForObjectHeight = height;
            ForObjectTaken++;
            ShowCaptures++;
            ShowAnswer = present ? "taken" : "taken (asked)";
            string size = width > .02f ? "about " + (width * 1000f).ToString("0") + " mm across" + (height > .01f ? ", " + (height * 1000f).ToString("0") + " mm tall" : "") : "";
            Talk.Add("The person's " + name + " was shown in Spark's frame" + (size.Length > 0 ? ": " + size + " (depth)" : ""));
            Note("measure", "Your " + name, size.Length > 0 ? size : "shown in Spark's frame", crop);
            Say(Crew.Spark, width > .02f ? "Got your " + name + "! About " + (width * 1000f).ToString("0") + " millimetres across." : "Got your " + name + "!");
            Body(Crew.Spark)?.Play("Peek");
            Heard();
            LeaveForObject();
        }

        // ShowPicture in a later step while the thing taken may still stand in the circle. True: handled here
        // (the same thing again is not the shape; the circle emptied only says so).
        private bool ForObjectStillThere(bool present, bool asked, List<Vector3> standing, float top)
        {
            if (!ForObjectInCircle || asked) return false;
            if (!present)
            {
                ForObjectInCircle = false;
                ShowAnswer = "emptied";
                ScanFailed("Okay, it's empty. Put in what you want to show me!");
                PulseFrame();
                return true;
            }
            float width = WidthAcrossView(standing);
            bool same = standing.Count >= 3 && ForObjectWidth > 0f && Mathf.Abs(top - ForObjectHeight) < .015f && Mathf.Abs(width - ForObjectWidth) < .02f;
            if (!same) { ForObjectInCircle = false; return false; }
            ShowAnswer = "still the " + ForObjectName;
            ScanFailed("That's still your " + ForObjectName + "! Take it out and put in something with the " + (Current == Step.ColourInput ? "colours" : "shape") + " you like.");
            PulseFrame();
            return true;
        }
    }
}
