using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;
using Pet = RoomCapsule.CapsulePetController;

namespace Windups.Handoff
{
    // F55 (2026-11-13, the user: "show the tools one by one when they say what they can do, then only when we come
    // close; no tools while a character is grabbed; the settings button can be on the black one"). Each teammate's
    // tool is a request you can touch, with no explanation panel:
    //  - Maker holds up its sketchbook: a fingertip tap (or a pinch aimed from afar) asks to draw. A drawing is taken
    //    only where it means something (DrawingWelcome: the shape step, the shape sketches, the shape review and v2);
    //    elsewhere Maker puts it off in one line.
    //  - Spark holds up its magnifier: a tap asks it to look around (Find ideas before anything is chosen). The
    //    person asked, so F45's "never look around unasked" still holds.
    //  - Fixer holds up a gear: a tap opens the settings box (MakerSession.Settings.cs).
    // When: the first team introduction shows each tool as its owner is introduced; after that a tool shows only
    // while a hand is near its owner (the nearest one, ToolReveal), and goes ToolLinger after the hand leaves. A
    // teammate that is grabbed or on a palm shows nothing, and after it is let go nothing comes back until the hands
    // have gone. Busy teammates (walking, working, looking, voting) show nothing. "What can you do?" makes all three
    // show their tool in turn with one line each; two one-time hints (drawing in the shape review, looking around
    // when the colours stall) do the same for one tool.
    // Runs four things a frame over at most 2 hands x 3 teammates: no lists, LINQ or strings per frame.
    public sealed partial class MakerSession
    {
        public const string ToolsTaughtKey = "thinkmade.toolsTaught", DrawHintKey = "thinkmade.drawHint", LookHintKey = "thinkmade.lookHint";
        public static float ToolReveal = .16f, ToolDwell = .15f, ToolLinger = 1.2f, ToolTapAfter = .35f, LookHintQuiet = 20f;
        public static bool ToolHintsWhenScripted; // the one-time hints and the first roll call stay out of scripted checks
        public static readonly Crew[] ToolOrder = { Crew.Maker, Crew.Fixer, Crew.Spark };

        public int ToolTaps { get; private set; }
        public int ToolReveals { get; private set; }
        public int CameToHand { get; private set; } // F101: teammates that came up to a hand (their tool stays away)
        public int RollCalls { get; private set; }
        public int DrawDeferrals { get; private set; }
        public int ToolHints { get; private set; }
        public string LastToolTap { get; private set; } = "";
        public Crew? ToolNear { get; private set; }
        public bool ToolBookShown => toolBook;
        public Transform Gear => gear;

        private readonly Dictionary<Crew, float> toolUntil = new Dictionary<Crew, float>();
        private readonly Dictionary<Crew, float> toolSince = new Dictionary<Crew, float>();
        private readonly HashSet<Crew> toolBlocked = new HashSet<Crew>();
        private readonly HashSet<Hand> toolTouchDown = new HashSet<Hand>();
        private readonly Dictionary<Hand, bool> toolPinchWas = new Dictionary<Hand, bool>();
        private Crew? toolCandidate;
        private float toolCandidateSince = -1f, finishQuietSince = -1f;
        private bool toolBook, wasChangingShape;
        private int toolClickFrame = -1;
        private Coroutine rollCall;

        public bool ToolShown(Crew crew) => toolUntil.TryGetValue(crew, out float until) && Time.time < until && ToolFree(crew);

        // The teammate is free to hold its tool up: standing still, its hands not in use.
        private bool ToolFree(Crew crew)
        {
            if (arrival != null && arrival.Offline) return false; // F78 airplane mode: Fixer holds out the post-its instead
            if (!cast.TryGetValue(crew, out var character) || character == null || character.movement == null) return false;
            var pet = character.movement;
            if (pet.IsHeld || (pet.State != Pet.MotionState.Idle && pet.State != Pet.MotionState.WaitingForRoom)) return false;
            var body = Body(crew);
            if (body == null || body.IntroActive || body.RigHand(true) == null || body.RigHand(false) == null) return false;
            if (steppingAside.Contains(crew) || walkingBack.Contains(pet) || stranded.Contains(pet)) return false;
            if (Comparing || InMeeting || (arrival != null && arrival.CallingOver)) return false;
            switch (crew)
            {
                case Crew.Maker: return !MakerWorking && !Drawing && !paperInFlight && !takingSketchbook && !SketchbookHeld && !OfferingBook;
                case Crew.Spark: return !looking && !SparkWalking && !(Eyes != null && Eyes.Looking);
                default: return true;
            }
        }

        // Shows `crew`'s tool for `seconds` (the introduction, the roll call, a hint).
        public void ShowToolFor(Crew crew, float seconds)
        {
            toolBlocked.Remove(crew);
            toolUntil.TryGetValue(crew, out float until);
            toolUntil[crew] = Mathf.Max(until, Time.time + seconds);
            WriteInputLog("tool shown " + crew + " " + seconds.ToString("0.#") + " s");
        }

        // The first team introduction (WindupsArrival.IntroduceTeam, before the session begins).
        public void ShowCrewTool(string characterName, float seconds)
        {
            EnsureToolProps();
            if (MakerRoles.TryForCharacter(characterName, out var crew)) ShowToolFor(crew, seconds);
        }

        public static void MarkToolsTaught() => MarkTaught(ToolsTaughtKey);

        // The crew, the tool props and the saved settings, also before the session begins (the introduction).
        private void EnsureToolProps()
        {
            if (arrival == null) return;
            var bubble = arrival.guide != null ? arrival.guide.authoredBubble : null;
            if (material == null && bubble != null) material = bubble.GetComponent<MeshRenderer>().sharedMaterial;
            if (font == null && bubble != null) font = bubble.GetComponent<SpeechBubblePresentation>()?.toyFont;
            if (Voice == null) Voice = GetComponent<StudioVoice>() ?? gameObject.AddComponent<StudioVoice>();
            foreach (var dock in arrival.handoff.docks)
                if (dock.character != null && MakerRoles.TryForCharacter(dock.character.characterName, out var crew)) cast[crew] = dock.character;
            if (tapeBar == null && material != null) BuildTools();
            if (gear == null && material != null) BuildGear();
            LoadSettings();
        }

        // ------------------------------------------------------------ every frame (Update, also before Begin)

        private void CrewToolsFrame()
        {
            if (cast.Count == 0 || arrival == null) return;
            float now = Time.time;
            bool reaching = Begun || arrival.CurrentPhase >= WindupsArrival.Phase.OfferingTopic;
            // Grabbed or on a palm: its tool goes at once and stays away until the hands have left it.
            foreach (var pair in cast)
            {
                if (pair.Value == null || pair.Value.movement == null || !pair.Value.movement.IsHeld) continue;
                if (toolBlocked.Add(pair.Key)) WriteInputLog("tool hidden " + pair.Key + " (held)");
                toolUntil[pair.Key] = 0f;
            }
            Crew? nearest = null;
            float best = float.MaxValue;
            bool handsBusy = Holding || Drawing || Capturing || SketchbookHeld || takingSketchbook || NotebookHeld || (carried != null) ||
                             (Begun && AnyHandWorkedWithin(CrewQuietAfter)); // F101: not while (or just after) a hand works
            if (reaching)
                foreach (var hand in HandSet.Both())
                {
                    if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || HandIO.Pinching(hand)) continue;
                    if (brushSwatch != null && hand == brushHand) continue; // painting
                    if (HandIO.Joint(hand, HandJointId.HandIndexTip, out var tip)) Closest(tip.position, ref nearest, ref best);
                    if (HandIO.Joint(hand, HandJointId.HandPalm, out var palm)) Closest(palm.position, ref nearest, ref best);
                }
#if UNITY_EDITOR
            // Editor preview: the mouse over a teammate stands in for a hand near it.
            if (reaching && !handsBusy && !UnityEngine.XR.XRSettings.isDeviceActive && !HandIO.Active && Camera.main != null && UnityEngine.InputSystem.Mouse.current is { } mouse)
            {
                var ray = Camera.main.ScreenPointToRay(mouse.position.ReadValue());
                foreach (var pair in cast)
                {
                    if (pair.Value == null || pair.Value.movement == null) continue;
                    float off = RayDistance(ray, ToolMiddle(pair.Value)) * (ToolReveal / .05f);
                    if (off < best) { best = off; nearest = pair.Key; }
                }
            }
#endif
            // F101 (the user: "the characters keep coming near my hands and odd things get pressed"): a teammate that walks
            // or hops up to a hand shows no tool until that hand has left it (as after being held).
            // The same for a teammate beside hands that are at work: nothing of it comes out when the work pauses.
            if (nearest.HasValue && best < ToolReveal * 1.8f && cast.TryGetValue(nearest.Value, out var comer) && comer != null && comer.movement != null &&
                (handsBusy || comer.movement.State != RoomCapsule.CapsulePetController.MotionState.Idle) && !comer.movement.IsHeld && toolBlocked.Add(nearest.Value))
            {
                toolUntil[nearest.Value] = 0f;
                CameToHand++;
                WriteInputLog("tool hidden " + nearest.Value + " (it came to the hand)");
            }
            // Let go: it shows again once no hand is near it.
            if (toolBlocked.Count > 0)
                foreach (var pair in cast)
                    if (toolBlocked.Contains(pair.Key) && pair.Value != null && !pair.Value.movement.IsHeld && !(nearest == pair.Key && best < ToolReveal * 1.8f))
                    { toolBlocked.Remove(pair.Key); WriteInputLog("tool free " + pair.Key); }
            Crew? near = !handsBusy && best <= ToolReveal ? nearest : null;
            if (near.HasValue && (toolBlocked.Contains(near.Value) || !ToolFree(near.Value))) near = null;
            if (near != toolCandidate) { toolCandidate = near; toolCandidateSince = now; }
            ToolNear = near;
            if (near.HasValue && now - toolCandidateSince >= ToolDwell)
            {
                toolUntil.TryGetValue(near.Value, out float until);
                if (until <= now) { ToolReveals++; WriteInputLog("tool near " + near.Value); }
                toolUntil[near.Value] = Mathf.Max(until, now + ToolLinger);
            }
            if (SettingsOpen && ToolFree(Crew.Fixer)) toolUntil[Crew.Fixer] = Mathf.Max(toolUntil.TryGetValue(Crew.Fixer, out float g) ? g : 0f, now + .5f);
            foreach (var crew in ToolOrder)
            {
                bool shown = ToolShown(crew);
                if (shown && !toolSince.ContainsKey(crew)) toolSince[crew] = now;
                else if (!shown) toolSince.Remove(crew);
            }
            MakerBookFrame();
            ToolTouches();
            if (!Begun) ToolPinchesBeforeBegin();
#if UNITY_EDITOR
            ToolClicks();
#endif
            SettingsFrame();
            if (Begun) ToolHintFrame();
        }

        private void Closest(Vector3 point, ref Crew? nearest, ref float best)
        {
            foreach (var pair in cast)
            {
                if (pair.Value == null || pair.Value.movement == null) continue;
                float d = Vector3.Distance(point, ToolMiddle(pair.Value));
                if (d < best) { best = d; nearest = pair.Key; }
            }
        }

        private static Vector3 ToolMiddle(WindupCharacter character) => character.movement.transform.position + Vector3.up * .075f;

        // ------------------------------------------------------------ Maker's sketchbook, shown

        // The same closed book and pose as the shape step's offer (PlaceOfferedBook), but only shown: a tap asks to draw.
        private void MakerBookFrame()
        {
            if (OfferingBook && toolBook) { toolBook = false; return; } // the shape step's offer took the book over
            bool show = ToolShown(Crew.Maker);
            if (show && !toolBook) StartToolBook();
            else if (!show && toolBook) StopToolBook(true);
        }

        private void StartToolBook()
        {
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null) return;
            BuildPaper();
            if (Drawing || takingSketchbook || paperInFlight || OfferingBook) return;
            toolBook = true;
            offeredBook = paper;
            offeredBookReady = false;
            offeredBookForward = .047f;
            if (sketchbookNear != null) sketchbookNear.enabled = false; // shown, not taken: a near pinch is Maker's grab
            paper.gameObject.SetActive(false);
            paper.localScale = Vector3.one * BookOfferScale;
            PlaceOfferedBook(maker, 1f);
            ToySounds.Play(ToySounds.Cue.Paper, maker.transform.position + Vector3.up * .08f, .25f, 1.6f);
        }

        private void StopToolBook(bool putAway)
        {
            if (!toolBook) return;
            toolBook = false;
            var body = Body(Crew.Maker);
            body?.Hold(true, null);
            body?.Hold(false, null);
            if (putAway && paper != null && !Drawing && !OfferingBook && !paperInFlight && paper.gameObject.activeSelf)
            {
                ToyFx.Puff(paper.position, .006f, 2);
                paper.gameObject.SetActive(false);
            }
        }

        // ------------------------------------------------------------ touching a tool

        // Where a shown tool can be touched, and how far from it.
        private bool ToolTarget(Crew crew, out Vector3 at, out float reach)
        {
            at = Vector3.zero;
            reach = 0f;
            if (!toolSince.TryGetValue(crew, out float since) || Time.time - since < ToolTapAfter) return false; // just appeared
            switch (crew)
            {
                case Crew.Maker:
                    if (!toolBook || paper == null || !paper.gameObject.activeInHierarchy) return false;
                    at = paper.position; reach = .034f; return true;
                case Crew.Spark:
                    if (magnifier == null || !magnifier.gameObject.activeInHierarchy) return false;
                    at = magnifier.position; reach = MagnifierRadius + .011f; return true;
                default:
                    if (gear == null || !gear.gameObject.activeInHierarchy) return false;
                    at = gear.position; reach = GearRadius + .011f; return true;
            }
        }

        // A fingertip on a shown tool (a pinch there belongs to the teammate's grab). F60: a free, pointing hand.
        private readonly FingerPress toolPress = new FingerPress();
        private void ToolTouches()
        {
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out var tip))
                {
                    toolPress.Forget(hand);
                    continue;
                }
                Crew? touched = null;
                foreach (var crew in ToolOrder)
                    if (ToolTarget(crew, out var at, out float reach) && Vector3.Distance(tip.position, at) <= reach) { touched = crew; break; }
                if (toolPress.Step(this, hand, touched != null)) { PressLog(touched.Value + "'s tool", hand); TapTool(touched.Value, "poke"); }
            }
        }

        // A pinch aimed at a shown tool from afar (OnPinch, once the session has begun).
        private bool CrewToolPinch(Vector3 point, Ray? ray)
        {
            if (!ray.HasValue) return false;
            foreach (var crew in ToolOrder)
            {
                if (!ToolTarget(crew, out var at, out float reach)) continue;
                if (RayDistance(ray.Value, at) > reach * .9f || Vector3.Distance(ray.Value.origin, at) < .12f) continue;
                if (Vector3.Distance(point, at) <= reach) continue; // a pinch right on it is the grab's
                TapTool(crew, "ray");
                return true;
            }
            return false;
        }

        // Before the session begins OnPinch does not run: the aimed pinches on tools and the settings box are read here.
        private void ToolPinchesBeforeBegin()
        {
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) { if (hand != null) toolPinchWas.Remove(hand); continue; }
                bool pinching = HandIO.Pinching(hand);
                toolPinchWas.TryGetValue(hand, out bool before);
                toolPinchWas[hand] = pinching;
                if (!pinching || before) continue;
                Vector3 point = PinchPoint(hand);
                Ray? ray = HandIO.Pointer(hand, out Pose pointer) ? new Ray(pointer.position, pointer.forward) : (Ray?)null;
                if (!SettingsPinch(point, ray)) CrewToolPinch(point, ray);
            }
        }

#if UNITY_EDITOR
        private void ToolClicks()
        {
            if (UnityEngine.XR.XRSettings.isDeviceActive || HandIO.Active || Camera.main == null || !(UnityEngine.InputSystem.Mouse.current is { } mouse) || !mouse.leftButton.wasPressedThisFrame) return;
            var ray = Camera.main.ScreenPointToRay(mouse.position.ReadValue());
            if (SettingsPinch(Vector3.positiveInfinity, ray) || CrewToolPinch(Vector3.positiveInfinity, ray)) toolClickFrame = Time.frameCount;
        }
#endif

        // Tests: a tap on a teammate's tool, as a fingertip would do it.
        public void SimulateToolTap(Crew crew) => TapTool(crew, "test");

        private void TapTool(Crew crew, string how)
        {
            ToolTaps++;
            LastToolTap = crew + " " + how;
            WriteInputLog("tool tap " + crew + " (" + how + ") step " + Current);
            ToySounds.Play(ToySounds.Cue.Press, ToolMiddle(cast[crew]) + Vector3.up * .05f, .35f, 1.7f);
            toolUntil[crew] = Time.time + ToolLinger + 1.5f;
            switch (crew)
            {
                case Crew.Maker: AskToDraw(); break;
                case Crew.Spark: AskToLook(); break;
                default: if (SettingsOpen) CloseSettings("gear"); else OpenSettings("gear"); break;
            }
        }

        private void AskToDraw()
        {
            if (!DrawingWelcome(out string later)) { DeferDrawing(later); return; }
            Heard();
            Talk.Add("The person tapped Maker's sketchbook.");
            StartCoroutine(HandSketchbook());
        }

        private void AskToLook()
        {
            if (Begun) Heard();
            if (Current == Step.Making && Begun) { Ack(Crew.Spark, "Once this is made, I'll look around!"); return; }
            if (looking || SparkWalking || (Eyes != null && Eyes.Looking)) { Ack(Crew.Spark, "I'm already looking!"); return; }
            Talk.Add("The person tapped Spark's magnifier.");
            // Before anything is chosen it is the Find ideas button (it also works at the first note).
            if (!Begun || (Current == Step.Suggesting && Ideas.Count == 0 && !InMeeting)) { FindIdeas("magnifier"); return; }
            LookAroundNow();
        }

        // ------------------------------------------------------------ where a drawing means something

        // The shape step, the white shape sketches being compared, the made shape's review and v2 (and the older
        // flow's Shape/Style steps, unchanged). Elsewhere Maker puts it off with `later`.
        public bool DrawingWelcome(out string later)
        {
            later = null;
            if (!Begun) { later = "Tell me what to make first, then we can draw it!"; return false; }
            if (!FormFirst) return true;
            switch (Current)
            {
                case Step.ShapeInput:
                case Step.Shape:
                case Step.Style:
                    return true;
                case Step.Looks:
                    if (lookJob == "forms" && LooksShown) return true;
                    later = lookJob == "finishes" ? "Let's draw after the colours!" : "Let me finish these sketches first!";
                    return false;
                case Step.Done:
                    if (Product != null && (Review == ReviewStage.Shape || (Review == ReviewStage.None && Draft != null))) return true;
                    later = "Let me finish this first, then we can draw!";
                    return false;
                case Step.Suggesting:
                case Step.ForObject:
                    later = "Tell me what to make first, then we can draw it!";
                    return false;
                case Step.Finish:
                case Step.ColourInput:
                    later = "Let's draw after the colours!";
                    return false;
                case Step.Measuring:
                    later = "Let's measure first, then draw!";
                    return false;
                default:
                    later = "Let me finish this first, then we can draw!";
                    return false;
            }
        }

        private void DeferDrawing(string later)
        {
            DrawDeferrals++;
            WriteInputLog("drawing put off at " + Current);
            Ack(Crew.Maker, later ?? "Not now, a little later!");
            Body(Crew.Maker)?.PlaySoon("HeadTilt", .2f);
        }

        // A drawing taken back by Maker outside the shape step (TakeDrawing): the shape sketches are drawn again with
        // it, or it becomes the person's flag on the next version (the review's picture edit gets the drawing too).
        private bool UseDrawing()
        {
            if (!FormFirst) return false;
            if (Current == Step.Looks && lookJob == "forms")
            {
                ShapeWords = (ShapeWords + ", shaped like the person's own drawing").Trim(' ', ',');
                Say(Crew.Maker, "Let me redraw them with your drawing!");
                StartCoroutine(MakeLooks("forms"));
                return true;
            }
            if (Current == Step.Done && Product != null && (Review == ReviewStage.Shape || Review == ReviewStage.None))
            {
                if (Review == ReviewStage.Shape) EnterChangeMode(false); // (F54: change mode first, then the flag)
                if (Draft == null) return false;
                PersonFlag("Follow my drawing", "whole shape");
                Say(Crew.Maker, "Your drawing is a flag on the next version!");
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------ "what can you do?" and the hints

        public static bool AsksWhatCrewDoes(string p) =>
            p.Contains("what can you do") || p.Contains("what do you do") || p.Contains("what can you guys do") || p.Contains("what can you all do") ||
            p.Contains("your tools") || p.Contains("what are those tools") || p.Contains("뭐 할 수 있") || p.Contains("뭘 할 수 있") ||
            p.Contains("무엇을 할 수") || p.Contains("도구 보여") || p.Contains("뭐 하는 애");

        private static string ToolLine(Crew crew) => crew switch
        {
            Crew.Maker => "I sketch and build. Tap my sketchbook to draw with me!",
            Crew.Spark => "I find ideas around you. Tap my magnifier!",
            _ => "I check that it fits. Tap my gear for settings."
        };

        public void RollCall(string trigger)
        {
            if (rollCall != null) StopCoroutine(rollCall);
            rollCall = StartCoroutine(RollCallRun(trigger));
        }

        private IEnumerator RollCallRun(string trigger)
        {
            RollCalls++;
            MarkTaught(ToolsTaughtKey);
            WriteInputLog("roll call (" + trigger + ")");
            Talk.Add("The crew showed what each of them can do.");
            foreach (var crew in ToolOrder)
            {
                if (!cast.TryGetValue(crew, out var c) || c == null) continue;
                Say(crew, ToolLine(crew));
                var who = crew;
                yield return ShowWith(() =>
                {
                    ShowToolFor(who, 4f);
                    if (Head != null) LookAt(who, Head.position, 3f);
                });
            }
            rollCall = null;
        }

        private bool HintsAllowed => !Scripted || ToolHintsWhenScripted;

        private void ToolHintFrame()
        {
            // Someone who had made things before this feature: the roll call once, when the crew is quiet.
            if (HintsAllowed && rollCall == null && !Taught(ToolsTaughtKey) && Time.time - beganAt > 6f && lines.Count == 0 &&
                !Voice.IsSpeaking && !Voice.Loading && !WindupBody.UserTalking && CurrentLesson == null && !InMeeting)
                RollCall("first time");
            // The shape review: the first time the person starts changing it, Maker lifts its sketchbook.
            bool changing = changingShape && Review == ReviewStage.Shape && Current == Step.Done;
            if (changing && !wasChangingShape && HintsAllowed && !Taught(DrawHintKey)) StartCoroutine(DrawHint());
            wasChangingShape = changing;
            // The colours stall (nothing picked, nobody talking, no lesson) for LookHintQuiet: Spark lifts its magnifier.
            bool quiet = Current == Step.Finish && CurrentLesson == null && FinishSwatches.Count == 0 && lines.Count == 0 &&
                         !Voice.IsSpeaking && !WindupBody.UserTalking && !looking && !SparkWalking && Sculpt == SculptStage.None;
            if (!quiet) finishQuietSince = -1f;
            else if (finishQuietSince < 0f) finishQuietSince = Time.time;
            else if (Time.time - finishQuietSince > LookHintQuiet && HintsAllowed && !Taught(LookHintKey) && ToolFree(Crew.Spark))
            {
                MarkTaught(LookHintKey);
                ToolHints++;
                WriteInputLog("hint: look around");
                Say(Crew.Spark, "Want colour ideas? Tap my magnifier and I'll look around!");
                StartCoroutine(ShowWith(() => ShowToolFor(Crew.Spark, 6f)));
            }
        }

        private IEnumerator DrawHint()
        {
            MarkTaught(DrawHintKey);
            ToolHints++;
            yield return new WaitForSeconds(1.6f); // after Maker's own line about changing it
            for (float until = Time.time + 10f; Time.time < until && !ToolFree(Crew.Maker); ) yield return null;
            if (!ToolFree(Crew.Maker) || Review != ReviewStage.Shape) yield break;
            WriteInputLog("hint: draw it");
            Say(Crew.Maker, "Or draw it for me: tap my sketchbook!");
            yield return ShowWith(() => ShowToolFor(Crew.Maker, 6f));
        }

        // ------------------------------------------------------------ the props' poses (LateUpdate, also before Begin)

        private void CrewToolsLate()
        {
            if (cast.Count == 0) return;
            float blend = 1f - Mathf.Exp(-Time.deltaTime * 8f);
            if (!Begun && magnifier != null) MagnifierFrame(); // (after Begin, LateUpdate runs it)
            if (toolBook && cast.TryGetValue(Crew.Maker, out var maker) && maker != null) PlaceOfferedBook(maker, blend);
            GearFrame();
            if (Head != null)
                foreach (var crew in ToolOrder)
                    if (ToolShown(crew) && !(crew == Crew.Spark && magnifierOwnsLook) && cast.TryGetValue(crew, out var c) && c != null) c.lookTarget = Head; // Spark peers through its lens
        }
    }
}
