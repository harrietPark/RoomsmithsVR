using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Windups.Handoff
{
    // F50: user decisions, never timers or a crew opinion, move a clay shape into finishing.
    public sealed partial class MakerSession
    {
        public enum ReviewStage { None, Shape, DrawingRevision, RevisionPicture, Placement, RevisionFailed }
        public ReviewStage Review { get; private set; }
        public bool ShapeConfirmed { get; private set; }
        public bool WaitingForPlacement => Review == ReviewStage.Placement;
        public Transform PlacementView => placementView;
        public Picture RevisionPicture => revisionPicture;
        public string ShapeRequirements { get; private set; } = "";
        public static float DecisionReminderSeconds = 30f;
        public static bool FailRevisionPictureInTests;
        private int reviewEpoch, decisionReminders;
        private float decisionRemindAt;
        private Picture revisionPicture, shapeReference;
        private Vector3? reviewPanelCenter;
        private Transform placementView;
        private bool placementApproved, placementCancelled, revisionExecuting, revisionPrepared, drawingRevision;
        private byte[] revisionJpeg;
        private string revisionChange;
        private float placementRadius;
        private Coroutine revisionSketch;
        private Vector3 approvedPlace;
        private Quaternion approvedTurn;
        private readonly Dictionary<Transform, (byte[] image, string task, MakerLook look)> shapeRecords =
            new Dictionary<Transform, (byte[], string, MakerLook)>();

        private void ResetReview()
        {
            reviewEpoch++;
            ShapeConfirmed = false;
            Review = ReviewStage.None;
            ShapeRequirements = "";
            revisionExecuting = revisionPrepared = drawingRevision = false;
            placementApproved = placementCancelled = false;
            revisionJpeg = null;
            revisionChange = null;
            ClearReviewPictures();
            if (placementView != null && placementView != pendingStand && placementView != projectPlate) DestroyStand(placementView);
            placementView = null;
            changingShape = false;
            crewFlagsFor = null;
            waiting = null;
            CloseQuestion("new make");
        }

        private void ClearReviewPictures()
        {
            if (revisionPicture?.root != null) Destroy(revisionPicture.root.gameObject);
            if (shapeReference?.root != null) Destroy(shapeReference.root.gameObject);
            revisionPicture = shapeReference = null;
            reviewPanelCenter = null;
            reviewSide.Clear();
            reviewMovedByPerson = false;
            reviewPush = reviewPushWant = 0f;
            if (panelGroup == PanelGroup.Review) EndPanel(false);
        }

        // Immediate, persistent action choices. They do not sit behind stale crew lines.
        private void Decision(string line, params Reply[] choices)
        {
            // F54: a teammate's open question is not pushed aside: the choice waits and comes back after the answer
            // (it used to be dropped here without a word). What the person just asked for comes first.
            if (Question != null && !Question.answered && Question.toCrew && Question.step == Current && !PersonActing)
            {
                Park(Crew.Maker, line, choices, true);
                return;
            }
            Hush();
            waiting = null;
            CloseQuestion("decision updated");
            Caption(Crew.Maker, line, 3600f);
            OpenQuestionCard(Crew.Maker, line, false, choices);
            Talk.Add("Maker: " + line);
            decisionRemindAt = Time.time + DecisionReminderSeconds;
            decisionReminders = 0;
        }

        private void EnterShapeReview()
        {
            if (!FormFirst || Product == null) return;
            ShapeConfirmed = false;
            finishDone = false;
            Current = Step.Done;
            Review = ReviewStage.Shape;
            revisionExecuting = revisionPrepared = false;
            placementApproved = false;
            // F54: right after a make only the made thing, its picture and two choices. The see-through next
            // version and the crew's flags come with Change it (or the first change the person says).
            changingShape = Draft != null; // (a draft left standing keeps its change choices)
            ShowShapeReference();
            if (!string.IsNullOrWhiteSpace(pendingChange))
            {
                string queued = pendingChange;
                pendingChange = null;
                EnterChangeMode();
                PersonFlag(queued);
            }
            else if (!OfferAutoResizeNow()) ShowShapeChoices(); // F88: a stretched make: Fixer asks to auto-resize first
        }

        // F54 (2026-11-13, the user after the Quest try: "after v1 four options came up - what is this?"): Change
        // shape / Use this shape / Preview next / Versions became two choices that always do something:
        //  - right after a make: Keep this shape (colouring) | Change it;
        //  - after Change it: v(n+1) stands see-through beside it, the crew stand their flags, and Draw v(n+1)
        //    appears only once something is planted or resized on it (and goes again when nothing is);
        //  - Versions only once there are two versions or more.
        private bool changingShape;
        private Transform crewFlagsFor;
        public bool ChangingShape => changingShape;
        // F77 (2026-11-14, the user: "Keep this shape / Draw v2 - what?"): the labels say what happens. Start colouring
        // keeps this shape and starts colouring; Change shape shows the see-through next version; Done (once something is
        // planted or resized on it) makes that version straight away. A new picture is shown first only when asked
        // ("draw it first", "show me the picture"). No version pills: the version on the stand is the one worked on.
        public const string StartColouringLabel = "Start colouring", ChangeShapeLabel = "Change shape", ShapeChangesDoneLabel = "Done";
        private const string KeepLine = "Here's v{0}. Start colouring, or change its shape?";
        private const string KeepLineVersions = "Here's v{0}! Put the one you like on the stand. Colour it, or change its shape?";
        private const string SwappedLine = "v{0} it is! Colour it, or change its shape?";
        private const string KeepLinePager = "Here's v{0}! Tap < on the stand to see v{1}. Colour it, or change its shape?"; // F94
        private const string ChangeLine = "Tell me what to change, or plant a flag on v{0}. Then tap Done.";
        private bool versionJustSwapped, revisionShowsPicture;
        // What the crew AI and the Director are told about the review (F77 wording).
        // F93: the Sculpt it sentence only while that (switched-off) mode is offered, so the AI never suggests it.
        private static string ShapeWorkflow => "Review white clay versions until the person taps Start colouring. " +
            (SculptChoice ? "Sculpt it lets the person shape the made thing by hand " +
                "(push in with the right finger, rub with the left finger to smooth; Done keeps it). " : "") +
            "Change shape shows a see-through next version; " +
            "flags planted on it (or its sizes pulled) and Done make that version straight away (its new picture is shown first only if the person asks). " +
            (PlatePager && VersionsIntoNotebook
                ? "The version standing on the wooden stand is the one worked on; the person swaps versions with the < > buttons on the stand (the see-through next version stands there too). "
                : "The version standing on the wooden stand is the one worked on; the person swaps versions by putting another one on the stand. ") +
            "Never start colour before Start colouring. Wait for required answers.";
        private int NextVersionNumber => Versions.Count > 0 ? Versions.Max(v => v.number) + 1 : 2;
        public bool RevisionShowsPicture => revisionShowsPicture;

        // "Draw it first", "show me the picture", "draw v2": the person wants to see the new picture before it is built.
        public static bool WantsPictureFirst(string p) => Regex.IsMatch(p ?? "",
            @"^(draw|preview)\b|\b(show|see)\b.{0,20}\b(picture|drawing|sketch|first)\b|\bpicture first\b|\bdraw (it|that|them|the changes?|my changes?) first\b|그림.{0,4}먼저|먼저.{0,4}(그려|보여)|그려\s*(줘|봐|주세요)");

        // Something planted or resized on the see-through next version (what Draw v2 would draw).
        public bool HasShapeChanges
        {
            get
            {
                if (Draft == null) return false;
                if (DraftSized) return true;
                for (int i = 0; i < Flags.Count; i++) if (Flags[i].place == FlagPlace.Planted) return true;
                return false;
            }
        }

        // F82 (Quest: "while v2 is being changed it asks Start colouring or Done - that doesn't make sense, only Done"):
        // while the shape is being changed the only button is Done (once something is planted or resized). false keeps
        // F77's Start colouring there too (older checks).
        public static bool ChangeDoneOnly = true;
        private Reply[] ShapeReplies()
        {
            if (changingShape && ChangeDoneOnly) return HasShapeChanges ? new[] { Rp(ShapeChangesDoneLabel, "done") } : new Reply[0];
            // F84 (the user: "after the shape is made and before colouring, let me sculpt it - shave a bit, stretch a bit"):
            // Sculpt it | Change shape | Start colouring (without sculpting: Start colouring | Change shape, as F77).
            if (!changingShape && SculptOffered)
                return new[] { Rp(SculptItLabel, "sculpt it"), Rp(ChangeShapeLabel, "change shape"), Rp(StartColouringLabel, "start colouring") };
            var replies = new List<Reply> { Rp(StartColouringLabel, "start colouring") };
            if (!changingShape) replies.Add(Rp(ChangeShapeLabel, "change shape"));
            else if (HasShapeChanges) replies.Add(Rp(ShapeChangesDoneLabel, "done"));
            return replies.ToArray();
        }

        private string ShapeLine()
        {
            int n = Versions.FirstOrDefault(v => v.root == Product)?.number ?? 1;
            if (changingShape) return string.Format(ChangeLine, NextVersionNumber);
            if (versionJustSwapped) { versionJustSwapped = false; return string.Format(SculptOffered ? SwappedLineSculpt : SwappedLine, n); }
            bool several = !VersionsIntoNotebook && projectPlate != null && Versions.Count(v => v.root != null) >= 2; // F81: earlier versions are in the notebook
            if (SculptOffered) return string.Format(several ? KeepLineVersionsSculpt : KeepLineSculpt, n);
            // F94: earlier versions are behind < on the stand.
            var earlier = Versions.Where(v => v.root != null && v.number < n).OrderByDescending(v => v.number).FirstOrDefault();
            if (PlatePagerOn && projectPlate != null && earlier != null) return string.Format(KeepLinePager, n, earlier.number);
            return string.Format(several ? KeepLineVersions : KeepLine, n);
        }

        private void ShowShapeChoices()
        {
            if (Product == null) return;
            if (Current == Step.Planning || Current == Step.Making || Sculpt != SculptStage.None) return; // F82: nothing to choose while the next one is made
            Decision(ShapeLine(), ShapeReplies());
        }

        // Change it: the see-through next version beside the made thing, and (once per made thing) the crew's flags.
        private void EnterChangeMode(bool show = true)
        {
            if (Product == null || Review != ReviewStage.Shape) return;
            changingShape = true;
            if (Draft == null) MakeDraft();
            if (show) ShowShapeChoices();
            if (crewFlagsFor != Product)
            {
                crewFlagsFor = Product;
                StartCoroutine(FlagsLater());
            }
        }

        // Every frame: Draw v2 shows exactly while there is something to draw (Maker's line stays as it is).
        private void ShapePillsFrame()
        {
            var q = Question;
            if (!changingShape || Review != ReviewStage.Shape || q == null || q.answered || q.toCrew || q.who != Crew.Maker ||
                !q.text.StartsWith("Tell me what to change", System.StringComparison.Ordinal)) return;
            bool drawShown = false;
            for (int i = 0; i < q.chips.Count; i++) if (q.chips[i].reply.say == "done") { drawShown = true; break; }
            if (drawShown != HasShapeChanges) ReplaceReplies(ShapeReplies());
        }

        private void ShowShapeReference()
        {
            ClearReviewPictures();
            if (FormJpeg == null) return;
            // F102: the chosen picture is the one picture of the shape: it shows this version's picture (no copy).
            var chosen = ChosenAtEyes ? ChosenPicture : null;
            if (chosen != null) { ShowOnChosen(chosen, FormJpeg); return; }
            shapeReference = ReviewImage(FormJpeg, "Approved shape", -.12f);
        }

        public int ShapeCopiesShown => shapeReference != null && shapeReference.root != null && revisionPicture == null ? 1 : 0; // (checks)

        private void ShowOnChosen(Picture chosen, byte[] jpeg)
        {
            if (chosen == null || jpeg == null || chosen.jpeg == jpeg) return;
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!tex.LoadImage(jpeg)) { Destroy(tex); return; }
            owned.Add(tex);
            if (chosen.image != null) { chosen.image.texture = tex; chosen.image.color = Color.white; }
            chosen.texture = tex;
            chosen.jpeg = jpeg;
        }

        private Picture ReviewImage(byte[] bytes, string title, float side)
        {
            var p = MakePicture(.17f, title);
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (bytes == null || !tex.LoadImage(bytes)) { Destroy(tex); Destroy(p.root.gameObject); return null; }
            owned.Add(tex);
            p.image.texture = tex;
            p.image.color = Color.white;
            p.jpeg = bytes;
            p.texture = tex;
            p.Done = true;
            if (!reviewPanelCenter.HasValue) reviewPanelCenter = VisiblePanelSpot(FrontPoint(.65f, -.02f, out _), .24f, .23f, .345f);
            Vector3 at = reviewPanelCenter.Value;
            Quaternion rot = SpeechBubbleDock.FacingAt(at);
            at += rot * Vector3.right * side;
            p.root.SetPositionAndRotation(at, rot);
            reviewSide[p] = side; // (2026-11-13) placed each frame from the pair's centre, which the person may carry
            return p;
        }

        private void RecordShape()
        {
            if (!FormFirst || Product == null || Design == null) return;
            if (!shapeRecords.ContainsKey(Product) || !ShapeConfirmed)
                shapeRecords[Product] = (FormJpeg ?? LookJpeg, ShapeTaskId, Look);
            var v = Versions.FirstOrDefault(x => x.root == Product);
            if (v == null)
            {
                v = new Version { number = Versions.Count == 0 ? 1 : Versions.Max(x => x.number) + 1,
                    root = Product, design = Design.Copy() };
                Versions.Add(v);
            }
            v.design = Design.Copy();
            v.tag = VersionTag(Product, "v" + v.number);
        }

        public bool SelectShapeVersion(int number)
        {
            if (!FormFirst || Review != ReviewStage.Shape || Holding) return false;
            var v = Versions.FirstOrDefault(x => x.number == number && x.root != null);
            if (v == null || !shapeRecords.TryGetValue(v.root, out var record)) return false;
            if (PlatePagerOn && projectPlate != null)
            {
                // F94: "use v1" and the like swap what stands on the stand, as < > do (a see-through next version stays).
                int now = Versions.FirstOrDefault(x => x.root == Product)?.number ?? number;
                if (v.root != Product || DraftOnPlate) PagerShowVersion(v, number < now || DraftOnPlate ? -1 : 1);
                if (!changingShape) versionJustSwapped = true;
                ShowShapeChoices();
                return true;
            }
            ClearFlags();
            DestroyDraft();
            if (projectPlate != null && v.root != Product && !SwitchPlateVersion(v)) return false;
            Product = v.root;
            Design = v.design.Copy();
            ProductMesh = Product.GetComponentInChildren<MeshFilter>().sharedMesh;
            productFinished = true;
            LookJpeg = FormJpeg = record.image;
            ShapeTaskId = record.task;
            Look = record.look;
            changingShape = false; // F54: the chosen version is shown with Start colouring | Change shape
            crewFlagsFor = null;
            versionJustSwapped = true; // F77: "v1 it is!"
            ShowShapeReference();
            ShowShapeChoices();
            return true;
        }

        // F77: no version pills; the version on the stand is the one worked on.
        private void ShowVersionChoices()
        {
            if (PlatePagerOn && projectPlate != null) { Caption(Crew.Maker, Versions.Count(v => v.root != null) >= 2 ? "Tap < or > on the stand to see them." : "There's only this one so far.", 6f); return; } // F94
            Caption(Crew.Maker, VersionsIntoNotebook ? (Versions.Count >= 2 ? "The earlier ones are in my notebook." : "There's only this one so far.")
                : Versions.Count(v => v.root != null) >= 2 && projectPlate != null
                ? "Put the one you like on the stand." : "There's only this one so far.", 6f);
        }

        public void ConfirmShape()
        {
            if (!FormFirst || Review != ReviewStage.Shape || Product == null || Holding) return;
            ShapeConfirmed = true;
            Review = ReviewStage.None;
            changingShape = false;
            if (shapeRecords.TryGetValue(Product, out var record))
            { FormJpeg = LookJpeg = record.image; ShapeTaskId = record.task; Look = record.look; }
            CloseQuestion("shape explicitly confirmed");
            ClearFlags();
            DestroyDraft();
            ClearReviewPictures();
            PutBrushDown();
            Note("decision", "Shape confirmed", "v" + (Versions.FirstOrDefault(v => v.root == Product)?.number ?? 1));
            StartFinish();
        }

        // Called before both speech routers and for known reply-button commands.
        private bool ReviewVoice(string text, string p)
        {
            if (!FormFirst) return false;
            if (p.Contains("start over") || p.Contains("처음부터")) return false;
            if (Review == ReviewStage.Placement)
            {
                if (p == "place here" || IsYes(p)) { ApprovePlacement(); return true; }
                if (p == "cancel placement" || p == "cancel") { placementCancelled = true; return true; }
                if (p == "move spot") { Caption(Crew.Maker, "Grab the stand and put it where you like.", 6f); return true; }
                Caption(Crew.Maker, "Move the stand if you like, then tap Place here.", 6f);
                return true;
            }
            if (Review == ReviewStage.RevisionPicture || Review == ReviewStage.RevisionFailed)
            {
                // 2026-11-13 (the user: "'Use this picture' / 'Change the picture' are hard - I wanted the new picture and
                // had to think which one; say use new picture, keep the previous one"): the pills are Use new picture /
                // Keep old one / Draw again; the old words still work.
                if (p == "back to changes" || p == "cancel" || p == "no" || KeepsOldPicture(p))
                { CancelRevisionReview(); return true; }
                if (p == "retry picture") { Review = ReviewStage.Shape; StartCoroutine(PreviewRevision(revisionShowsPicture)); return true; }
                if (Review == ReviewStage.RevisionPicture && (p == "use this picture" || UsesNewPicture(p) || IsYes(p)))
                { StartCoroutine(ApproveRevision()); return true; }
                if (p == "change picture" || p == "draw again" || p == "redraw" || p == "redraw it" || p == "draw it again")
                { CancelRevisionReview(); Caption(Crew.Maker, "Tell me what the new picture should change.", 8f); return true; }
                if (!QuestionLike(p) && !JustReaction(p) && !SaysDone(p))
                { CancelRevisionReview(); PersonFlag(text); StartCoroutine(PreviewRevision(true)); return true; }
                return true;
            }
            if (Review == ReviewStage.DrawingRevision)
            {
                Caption(Crew.Maker, revisionShowsPicture ? "I'm drawing your changes. You'll check the picture before I build." : "I'm making your changes now.", 6f);
                return true;
            }
            if (p == "return to shape" && Current == Step.Done && Product != null) { EnterShapeReview(); return true; }
            if (Review != ReviewStage.Shape) return false;
            if (p == "choose version" || p == "more versions") { ShowVersionChoices(); return true; }
            if (p == "back to shape") { ShowShapeChoices(); return true; }
            var v = Regex.Match(p, @"^(?:select|choose|use|show)\s+(?:version\s*|v)(\d+)$");
            if (v.Success) { SelectShapeVersion(int.Parse(v.Groups[1].Value)); return true; }
            if (p == "keep this shape" || p == "keep the shape" || p == "use this shape" || p == "start colouring" || p == "start coloring" ||
                p == "colour it" || p == "color it" || p == "모양 확정" || p == "색칠 시작")
            { ConfirmShape(); return true; }
            // F54: Change shape (old words still work) brings the see-through next version and the crew's flags.
            if (p == "change it" || p == "change shape" || p == "change the shape" || p == "모양 바꾸기")
            { EnterChangeMode(); return true; }
            // F77: Done makes the next version straight away; "draw it first" / "show me the picture" / "draw v2" shows the
            // new picture before it is built. With nothing planted it says so.
            bool pictureFirst = WantsPictureFirst(p) && (changingShape || HasShapeChanges);
            if (pictureFirst || p == "draw next version" || p == "preview next version" || p == "preview v2" || WantsV2(p) || (SaysDone(p) && HasShapeChanges))
            {
                if (!changingShape) EnterChangeMode(false);
                if (!HasShapeChanges) { ShowShapeChoices(); return true; }
                StartCoroutine(PreviewRevision(pictureFirst || p.StartsWith("draw") || p.StartsWith("preview")));
                return true;
            }
            if (SaysDone(p) || IsYes(p)) { ShowShapeChoices(); return true; }
            if (GlobalShapeRequest(p)) { if (!changingShape || Draft == null) EnterChangeMode(); PersonFlag(text, "whole shape"); return true; }
            return false;
        }

        public const string UseNewPictureLabel = "Use new picture", KeepOldPictureLabel = "Keep old one", DrawAgainLabel = "Draw again";
        public static bool UsesNewPicture(string p) => Regex.IsMatch(p ?? "", @"^(use|take|build|make)?\s*(the\s+)?new(\s+(one|picture|shape))?$|^use new picture$");
        public static bool KeepsOldPicture(string p) => Regex.IsMatch(p ?? "", @"^(keep|use)\s+(the\s+)?(old|previous|first)(\s+(one|picture|shape))?$|^keep old picture$");

        public static bool GlobalShapeRequest(string p) => Regex.IsMatch(p ?? "",
            @"\b(full|whole|entire|silhouette|outline|star|triangle|square|heart)[ -]|\b(star|triangle|square|heart)\b|전체|별\s*모양|완전한", RegexOptions.IgnoreCase);

        private UnityEngine.Networking.UnityWebRequest RevisionRequest(byte[] jpeg, string change)
        {
            var refs = new List<(byte[] jpeg, string role)>();
            if (jpeg != null) refs.Add((jpeg, "the previous object, to be revised"));
            if (DrawingJpeg != null) refs.Add((DrawingJpeg, "the person's intended complete outline; preserve its points and connected silhouette unless the latest instruction replaces it"));
            string prompt = "Function/context: " + Need + ". Shape requirements: " + ShapeBrief() + ". Latest whole-shape correction (takes priority over the old picture): " + ShapeRequirements + ". Requested changes: " + change + "." + RulesLine() + " Keep the entire object visible. Whole-shape changes apply to the complete silhouette, never just the named face. Preserve all tips; no cropped or missing parts. Ignore colour/material suggestions until the shape is approved.";
            return refs.Count > 0 ? EditRequest(prompt, refs, MakerRoles.ClayStyle) : GenerateRequest(prompt, MakerRoles.ClayStyle);
        }

        private string RevisionPlan()
        {
            var flags = Flags.Where(f => f.place == FlagPlace.Planted && !f.SizeOnly).ToList();
            return string.Join(" ", flags.Select((f, i) => (i + 1) + ". " + f.text.TrimEnd('.') +
                (f.part == "whole shape" || GlobalShapeRequest(f.text) ? " (whole object silhouette)." : " (" + Where(f) + ")."))) +
                (DraftSized ? " Size: " + SizeWords() + "." : "");
        }

        // F77 (2026-11-14, the user: "after v2 feedback it draws again - too long and repetitive; unless I ask it to draw,
        // just change v2"): `showPicture` false (Done) makes the next version straight away: Maker still sketches while the
        // new picture is made (Meshy builds from a picture), but nobody is asked to approve it and no stand is asked for
        // (it goes on the project's stand, the last version steps aside). Sizes only: no picture at all. `showPicture`
        // (asked for) shows Old picture / New picture as before.
        private IEnumerator PreviewRevision(bool showPicture = false)
        {
            if (Review != ReviewStage.Shape || Draft == null || Design == null) yield break;
            PagerShowDraft(true); // F94: Done shows what is being made on the stand
            revisionChange = RevisionPlan();
            if (string.IsNullOrWhiteSpace(revisionChange)) { ShowShapeChoices(); yield break; }
            revisionShowsPicture = showPicture;
            int epoch = reviewEpoch;
            CloseQuestion("making revision");
            Hush();
            ClearReviewPictures();
            revisionPrepared = false;
            revisionJpeg = null;
            if (!Flags.Any(f => f.place == FlagPlace.Planted && !f.SizeOnly))
            {
                // Sizes only: the same shape at the new size (no picture, no Meshy).
                RevisionsWithoutPicture++;
                revisionJpeg = FormJpeg ?? LookJpeg;
                Review = ReviewStage.RevisionPicture;
                yield return ApproveRevision();
                yield break;
            }
            Review = ReviewStage.DrawingRevision;
            drawingRevision = true;
            Current = Step.Planning;
            Say(Crew.Maker, showPicture ? "I'll draw these changes first: " + revisionChange : "Making v" + NextVersionNumber + " with your changes!");
            if (!Sketching) revisionSketch = StartCoroutine(Sketch());
            byte[] result = null;
            try
            {
                if (Scripted)
                {
                    // (F52) As with a real picture (10 s or more), it arrives once Maker has begun drawing: Maker may
                    // first have to step aside from the made thing on its stand to have room for the sketchbook.
                    for (float waited = 0f; waited < 5f && (waited < 1.5f || !Sketching); waited += Time.deltaTime) yield return null;
                    if (!FailRevisionPictureInTests) result = FormJpeg ?? LookJpeg;
                }
                else yield return EditPicture(FormJpeg ?? LookJpeg, revisionChange, r => result = r);
            }
            finally { drawingRevision = false; sketchWanted = false; }
            while (Sketching && epoch == reviewEpoch) yield return null;
            if (epoch != reviewEpoch) yield break;
            Current = Step.Done;
            if (result == null)
            {
                Review = ReviewStage.RevisionFailed;
                Decision("The revised picture didn't arrive. Your model and flags are safe.",
                    Rp("Retry picture", "retry picture"), Rp("Back to changes", "back to changes"));
                yield break;
            }
            revisionJpeg = result;
            if (!showPicture)
            {
                RevisionsWithoutPicture++;
                Review = ReviewStage.RevisionPicture; // (what ApproveRevision builds from)
                yield return ApproveRevision();
                yield break;
            }
            shapeReference = ReviewImage(FormJpeg ?? LookJpeg, "Old picture", -.105f);
            revisionPicture = ReviewImage(result, "New picture", .105f);
            Review = ReviewStage.RevisionPicture;
            Decision("Old picture on the left, new one on the right. Which one should I build?",
                Rp(UseNewPictureLabel, "use new picture"), Rp(KeepOldPictureLabel, "keep old picture"), Rp(DrawAgainLabel, "draw again"));
        }

        private IEnumerator ApproveRevision()
        {
            if (Review != ReviewStage.RevisionPicture || revisionJpeg == null || Draft == null) yield break;
            int epoch = reviewEpoch;
            // F77: the next version goes on the project's stand without asking (the last one steps aside); only
            // without a stand is the person asked where.
            float radius = Mathf.Max(DraftSizeMm.x, DraftSizeMm.z) * .0005f;
            if (!AutoPlaceVersion(radius)) yield return ChoosePlacement(Draft.position, Draft.rotation, radius);
            if (epoch != reviewEpoch || !placementApproved) { CancelRevisionReview(); yield break; }
            Draft.SetPositionAndRotation(approvedPlace, approvedTurn);
            LookJpeg = FormJpeg = revisionJpeg;
            revisionPrepared = true;
            revisionExecuting = true;
            ShapeConfirmed = finishDone = false;
            Review = ReviewStage.None;
            ClearReviewPictures();
            BuildFromDraft();
        }

        private void CancelRevisionReview()
        {
            ClearReviewPictures();
            revisionPrepared = revisionExecuting = false;
            placementApproved = false;
            revisionJpeg = null;
            if (Product != null && shapeRecords.TryGetValue(Product, out var record))
            { FormJpeg = LookJpeg = record.image; ShapeTaskId = record.task; }
            Current = Step.Done;
            Review = ReviewStage.Shape;
            ShowShapeReference();
            ShowShapeChoices();
        }

        private void FinishRevisionCommit()
        {
            if (!revisionExecuting) return;
            foreach (var f in Flags.Where(f => f.place == FlagPlace.Planted).ToList()) StartCoroutine(FlyToMaker(f));
            foreach (var f in Flags.Where(f => f.place == FlagPlace.Offered).ToList()) RemoveFlag(f, true);
            DestroyDraft();
            revisionExecuting = false;
            revisionPrepared = false;
        }

        private void RevisionFailed()
        {
            revisionExecuting = revisionPrepared = false;
            nextAsVersion = false;
            draftFrom = archiveDesign = null;
            versionSpot = null;
            if (Draft != null) Draft.gameObject.SetActive(true);
            if (Product != null) CancelRevisionReview();
        }

        // F52: the place is a wooden display stand (MakerSession.Stand.cs) instead of the blue footprint: it pops up
        // on a clear spot of the desk, the person may carry it anywhere, and the make goes onto it. `strict` (a
        // repaint): the stand must stand where Maker can work on it.
        private IEnumerator ChoosePlacement(Vector3 at, Quaternion turn, float radius, bool strict = false)
        {
            placementApproved = placementCancelled = false;
            placementRadius = Mathf.Clamp(radius, .03f, .6f);
            int epoch = reviewEpoch;
            Review = ReviewStage.Placement;
            if (placementView != null && placementView != pendingStand && placementView != projectPlate) DestroyStand(placementView);
            placementView = ShowStand(at, turn, placementRadius, strict);
            // F60: no Cancel pill (a hand carrying the stand pressed it and the stand vanished); "cancel" still works.
            Decision(strict ? "I'll paint it on this stand. Move it if you like, then tap Place here." : "Here's its stand. Move it anywhere, then tap Place here.",
                Rp("Place here", "place here"));
            while (epoch == reviewEpoch && !placementApproved && !placementCancelled) yield return null;
            if (epoch != reviewEpoch) yield break;
            if (held == placementView) LetGo();
            if (!placementApproved && placementView != null && placementView != projectPlate) DestroyStand(placementView);
            placementView = null;
            CloseQuestion("placement ended");
            Review = ReviewStage.None;
        }

        // F77: a next version needs no "Place here": it goes on the project's stand (as Place here on it did).
        public int RevisionsWithoutPicture { get; private set; }
        private bool AutoPlaceVersion(float radius)
        {
            placementApproved = placementCancelled = false;
            placementRadius = Mathf.Clamp(radius, .03f, .6f);
            standStrict = false;
            if (projectPlate == null || !ApproveStand(projectPlate)) return false;
            placementApproved = true;
            return true;
        }

        private Vector3 FindPlacement(Vector3 from, float radius)
        {
            var probes = new List<Vector3> { from };
            for (float r = .12f; r <= 1f; r += .1f)
                for (int i = 0; i < 16; i++) probes.Add(from + Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward * r);
            foreach (var p in probes)
            {
                if (!DeskTop(p, out float y)) continue;
                var at = new Vector3(p.x, y, p.z);
                if (PlacementClear(at, radius)) return at;
            }
            return from; // shown as a proposal only; ApprovePlacement never accepts an occupied place
        }

        public bool PlacementClear(Vector3 at, float radius)
        {
            if (WorkSurface != null && !OnWorkDesk(at, radius + .01f)) return false;
            return IntoZones(DeskObstacles(true), at, radius + .02f) <= 0f && HasWorkStand(at, radius);
        }

        // F52: wherever the stand is (MakerSession.Stand.cs decides where Maker works).
        private void ApprovePlacement()
        {
            if (placementView == null || held == placementView) return;
            if (ApproveStand(placementView)) placementApproved = true;
        }

        private void ReviewFrame()
        {
            if (FormFirst) ShapePillsFrame(); // F54
            if (!FormFirst || Question == null || Question.toCrew || Holding || Drawing || CurrentLesson != null || NotebookOpen ||
                crewBusy || WindupBody.UserTalking || lines.Count > 0 || Voice.IsSpeaking || Voice.Loading) return;
            if (Time.time < decisionRemindAt || Time.time - lastHeardAt < DecisionReminderSeconds || decisionReminders >= 2) return;
            if (Review != ReviewStage.Shape && Review != ReviewStage.RevisionPicture && Review != ReviewStage.Placement) return;
            decisionReminders++;
            decisionRemindAt = Time.time + DecisionReminderSeconds * 2f;
            Say(Crew.Maker, Review == ReviewStage.Shape
                    ? (!changingShape ? "Happy with this shape? Tap Start colouring, or Change shape."
                        : HasShapeChanges ? "Tap Done to make v" + NextVersionNumber + ", or start colouring."
                        : "Plant a flag on v" + NextVersionNumber + " or tell me what to change. Or start colouring.")
                : Review == ReviewStage.RevisionPicture ? "Tap Use new picture to build it, or Keep old one."
                : "Move the stand if you like, then tap Place here.");
        }
    }
}
