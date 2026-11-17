using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Windups.Handoff
{
    public sealed partial class MakerSession
    {
        private enum EndingState { Working, Confirming, Celebrating, Archived }
        private EndingState ending;
        private Transform endingLastProduct;
        private CoasterDesign endingLastDesign;
        private Transform projectPlate;
        private Transform promptedProduct;
        private float finishIdleSince;
        private bool finishIdleAsked;

        public bool WorkArchived => ending == EndingState.Archived;
        private bool FinishReady => Product != null && productFinished &&
            (Current == Step.Done || Current == Step.Finish) && Review == ReviewStage.None &&
            (finishDone || Product.GetComponentInChildren<PaintableThing>()?.Marks.Count > 0);

        public static bool IsCompletionCommand(string text)
        {
            string command = Regex.Replace((text ?? "").Trim().ToLowerInvariant().Replace('’', '\''), @"[\s.!?。]+$", "");
            return command == "i'm done" || command == "im done" || command == "i am done" ||
                command == "i'm finished" || command == "im finished" || command == "i am finished" ||
                command == "that's it" || command == "thats it" || command == "all done" ||
                command == "다 했어" || command == "다 만들었어" || command == "다했어" || command == "다만들었어";
        }

        private bool EndingVoice(string words)
        {
            if (WorkArchived && MakeAnotherVoice(words)) return true; // F98: Make another / Not now, "make a new one"
            if (words == "get my creation" || (WorkArchived && (words == "export" || words == "export it" || words == "export this" || words == "print it" || words == "내보내기" || words == "내보내 줘" || words == "내보내줘")))
            {
                GetCreation(NotebookWorks.FirstOrDefault(w => w.id == project));
                return true;
            }
            if (words == "not now" && WorkArchived)
            {
                CloseQuestion("web pickup deferred");
                AskMakeAnother(); // F98
                return true;
            }
            if (words == "still working") { finishIdleAsked = true; return true; }
            // F87: "I'm done" / "done painting" while colouring keeps the person's own paint (never "It looks unfinished").
            if (ending == EndingState.Working && PaintingStep && FinishedPaintingWords(words)) return ColouringVoice(words);
            if (ending == EndingState.Confirming)
            {
                if (words == "finish here" || words == "yes" || words == "yeah" || words == "응" || words == "네")
                { StartEnding(); return true; }
                if (words == "keep working" || words == "no" || words == "아니" || words == "아니야")
                {
                    ending = EndingState.Working;
                    CloseQuestion("keep working");
                    finishIdleAsked = true;
                    return true;
                }
            }
            if (IsCompletionCommand(words) || words == "finish creation")
            {
                if (ending != EndingState.Working) return true;
                if (FinishReady) StartEnding();
                else
                {
                    ending = EndingState.Confirming;
                    CloseQuestion("completion confirmation");
                    AskWith(Crew.Maker, "It looks unfinished. Finish here anyway?", false,
                        Rp("Finish here"), Rp("Keep working"));
                }
                return true;
            }
            if (ending == EndingState.Celebrating) return true;
            if (WorkArchived)
            {
                if (NotebookVoice(words)) return true;
                if (words == "edit this" || WantsVersion(words) || words.StartsWith("change ", StringComparison.Ordinal))
                {
                    ReopenWork(NotebookWorks.FirstOrDefault(w => w.id == project));
                    return words == "edit this";
                }
                return true;
            }
            return false;
        }

        private void FinishActivity()
        {
            finishIdleSince = Time.time;
            finishIdleAsked = false;
            if (WorkArchived)
            {
                ending = EndingState.Working;
                var work = NotebookWorks.FirstOrDefault(w => w.id == project);
                if (work != null) { work.completed = false; SaveNotebook(); }
                CloseQuestion("work changed");
            }
        }

        private void EndingFrame()
        {
            if (ending != EndingState.Working || !FinishReady) return;
            if (Holding || Drawing || Capturing || NotebookOpen || PaletteOnPalm || freeTape || collectingCreation ||
                SizingWithHands || userWasTalking || lines.Count > 0 || Voice.Loading || Voice.IsSpeaking)
            {
                finishIdleSince = Time.time;
                return;
            }
            if (promptedProduct != Product)
            {
                promptedProduct = Product;
                FinishActivity();
                Say(Crew.Maker, "Looking good! Tell me when you're done.");
            }
            else if (!finishIdleAsked && Time.time - Mathf.Max(finishIdleSince, lastHeardAt) >= 180f)
            {
                finishIdleAsked = true;
                CloseQuestion("completion check");
                AskWith(Crew.Maker, "All done, or still working?", false, Rp("Finish", "finish creation"), Rp("Keep working", "still working"));
            }
        }

        private void StartEnding()
        {
            if (ending == EndingState.Celebrating || WorkArchived) return;
            var keep = productFinished ? Product : endingLastProduct;
            var keepDesign = (Current == Step.Planning || Current == Step.Making) && endingLastProduct == keep
                ? endingLastDesign : productFinished ? Design : endingLastDesign;
            FlushNotebookSnapshot();
            StopAllCoroutines();
            Printing = false;
            StopLessons();
            ResetReview();
            AsideInputsAndSculpt();
            CloseQuestion("work finalized");
            Hush();
            Voice.Stop();
            working.Clear();
            ResetCrew();
            buildFrom = -1f;
            if (hiddenProduct != null) { hiddenProduct.gameObject.SetActive(true); hiddenProduct = null; }
            ClearLooks();
            ClearIdeas();
            HideIdeasButton();
            ClearFlags();
            DestroyDraft();
            ShowTape(false);
            freeTape = false;
            pendingChange = null;
            nextAsVersion = false;
            if (Product != null && Product != keep)
            {
                if (held == Product) LetGo();
                Destroy(Product.gameObject);
            }
            Product = keep;
            ProductMesh = keep != null ? keep.GetComponentInChildren<MeshFilter>()?.sharedMesh : null;
            Design = keepDesign?.Copy();
            productFinished = Product != null;
            if (Product != null) Product.gameObject.SetActive(true);
            if (pendingStand != null && pendingStand != projectPlate && Empty(pendingStand)) DestroyStand(pendingStand);
            pendingStand = null;
            Current = Step.Done;
            ending = EndingState.Celebrating;
            LoadNotes();
            if (project == null) NewProject();
            if (Product != null) Remember();
            var work = NotebookWorks.FirstOrDefault(w => w.id == project);
            if (work == null)
            {
                work = new NotebookWork { id = project, name = Design?.name ?? Need, at = DateTime.Now.ToString("dd MMM yyyy") };
                NotebookWorks.Insert(0, work);
            }
            work.topic = Need;
            work.completed = true;
            work.finalNameReady = false;
            work.completedOn ??= DateTime.Now.ToString("yyyy-MM-dd");
            work.stl = work.colourFile = work.glb = null;
            work.pickupCode = work.pickupUrl = null;
            SaveNotebook();
            RecordFinishedWall(work);
            StartCoroutine(PlayEnding(work));
        }

        private IEnumerator PlayEnding(NotebookWork work)
        {
            var thing = Product;
            if (thing != null)
            {
                Vector3 target = thing.position;
                Vector3 side = Head != null ? Head.right : Vector3.right;
                side.y = 0f;
                foreach (var pair in cast)
                {
                    var pet = pair.Value.movement;
                    float offset = pair.Key == Crew.Spark ? -.24f : pair.Key == Crew.Fixer ? .24f : 0f;
                    Vector3 approach = target + side.normalized * offset;
                    if (Mathf.Abs(offset) < .01f) approach += Vector3.Cross(side.normalized, Vector3.up) * .24f;
                    if (pet != null && !pet.IsHeld && WalkableNear(pet, approach, out var seat)) pet.GoTo(seat, 2f);
                }
                yield return new WaitForSeconds(1.1f);
                foreach (var crew in cast.Keys) { Body(crew)?.Play("Cheer"); LookAt(crew, target, 4f); }
                ToyFx.Confetti(target + Vector3.up * .10f, 25);
                Celebrate(target); // F82: a confetti shower over the finished work, in front of the person
                ToySounds.Play(ToySounds.Cue.Chime, target, .85f, 1f);
                Say(Crew.Maker, "You made it!");
                yield return new WaitForSeconds(1.25f);
                ShowIntroTool("Creativity", "frame", 1.2f);
                yield return new WaitForSeconds(.65f);
                string photo = Path.Combine(NotesFolder, work.id + "_finished_" + Guid.NewGuid().ToString("N") + ".jpg");
                Directory.CreateDirectory(NotesFolder);
                yield return NotebookPhotoLater(thing, photo);
                if (File.Exists(photo))
                {
                    work.photo = photo;
                    work.finishedPhotos.Add(photo);
                    SaveNotebook();
                    ToySounds.Play(ToySounds.Cue.Scan, thing.position, .6f, 1.15f);
                }
                yield return NameFinishedWork(work, thing, Design);
                OpenNotebook();
                if (File.Exists(photo) && notebookView != null) yield return FlyFinishedPhoto(thing.position, notebookView, photo);
                SelectNotebookWork(NotebookWorks.IndexOf(work));
                RecordFinishedWall(work);
                const string taught = "roomsmiths.journey.taught";
                if (!PlayerPrefs.HasKey(taught))
                {
                    Say(Crew.Maker, "Tap your note to see the journey. You can move the note, notebook and creation wherever you like.");
                    PlayerPrefs.SetInt(taught, 1);
                }
                else Say(Crew.Maker, "I added the new photo to your notebook and note.");
                yield return new WaitForSeconds(3f);
            }
            else
            {
                RecordFinishedWall(work);
                Say(Crew.Maker, "I saved your ideas in my notebook.");
            }
            ending = EndingState.Archived;
            RetireStandsAfterFinish(); // F105: one stand in the app; the finished work stays where it is, without it
            if (thing != null)
                AskWith(Crew.Fixer, "Export your 3D and print files to the web?", false, Rp(ExportLabel, "export"), Rp("Not now")); // F77: Export
            else AskMakeAnother(); // F98
        }

        // Name the actual final render, not the label of the picture chosen before modelling.
        // A failed request leaves the saved name intact and is retried by Export.
        private IEnumerator NameFinishedWork(NotebookWork work, Transform thing, CoasterDesign design)
        {
            if (work == null || thing == null || design == null || work.finalNameReady || Scripted) yield break;
            byte[] photo = null;
            try { if (File.Exists(work.photo)) photo = File.ReadAllBytes(work.photo); }
            catch (Exception e) { Debug.LogWarning("[Roomsmiths] final name photo: " + e.Message); }
            if (photo == null || photo.Length == 0) yield break;
            string json = null;
            yield return Ask(Crew.Maker, "name", new { topic = work.topic, dimensions = work.dimensions },
                value => json = value, false, new[] { photo }, false);
            if (thing == null || !work.completed || json == null) yield break;
            string name = null;
            try { name = MakerRoles.Clean((string)Newtonsoft.Json.Linq.JObject.Parse(json)["name"]); }
            catch (Exception e) { Debug.LogWarning("[Roomsmiths] final name response: " + e.Message); }
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Split(' ').Length > 4) yield break;
            ApplyFinishedName(work, thing, design, name);
        }

        private void ApplyFinishedName(NotebookWork work, Transform thing, CoasterDesign design, string name)
        {
            var memory = LoadMemory();
            // Resolve by immutable ID before changing the legacy name lookup.
            var item = SavedItem(memory, thing, design.name);
            design.name = name;
            if (madeDesigns.TryGetValue(thing, out var stored)) stored.name = name;
            madeNames[thing] = name;
            if (thing == Product && Design != null) Design.name = name;
            if (thing == endingLastProduct && endingLastDesign != null) endingLastDesign.name = name;
            if (item != null)
            {
                item.name = name;
                if (item.design != null) item.design.name = name;
                try { File.WriteAllText(MemoryPath, Newtonsoft.Json.JsonConvert.SerializeObject(memory, Newtonsoft.Json.Formatting.Indented)); }
                catch (Exception e) { Debug.LogWarning("[Roomsmiths] final name memory: " + e.Message); }
            }
            work.name = name;
            work.finalNameReady = true;
            SaveNotebook();
            RecordFinishedWall(work);
        }

        private int NextStoredVersionNumber() => Versions.Count == 0 ? 1 : Versions.Max(v => v.number) + 1;

        private void ReopenWork(NotebookWork work, Transform touched = null)
        {
            if (work == null || ending == EndingState.Celebrating) return;
            var thing = touched ?? MadeThings().FirstOrDefault(t => notebookOwners.TryGetValue(t, out var id) && id == work.id && t == Product)
                ?? MadeThings().FirstOrDefault(t => notebookOwners.TryGetValue(t, out var id) && id == work.id && StandUnder(t) != null)
                ?? MadeThings().FirstOrDefault(t => notebookOwners.TryGetValue(t, out var id) && id == work.id);
            if (thing == null || !madeDesigns.TryGetValue(thing, out var design)) return;
            if (Product != null && Product != thing)
            {
                Remember();
                if (!RoomItems.Contains(Product)) RoomItems.Add(Product);
                foreach (var version in Versions)
                    if (version.root != null && version.root != thing && !RoomItems.Contains(version.root)) RoomItems.Add(version.root);
                Versions.Clear();
            }
            RoomItems.Remove(thing);
            if (Kept == thing) Kept = null;
            var earlier = MadeThings().Where(t => t != thing && notebookOwners.TryGetValue(t, out var owner) && owner == work.id).ToList();
            foreach (var t in earlier)
            {
                RoomItems.Remove(t);
                if (Versions.Any(v => v.root == t) || !madeDesigns.TryGetValue(t, out var versionDesign)) continue;
                int number = NextStoredVersionNumber();
                Versions.Add(new Version { root = t, design = versionDesign.Copy(), number = number, tag = VersionTag(t, "v" + number) });
            }
            Product = thing;
            ProductMesh = thing.GetComponentInChildren<MeshFilter>()?.sharedMesh;
            ProductBody = thing.GetComponent<Rigidbody>();
            Design = design.Copy();
            project = work.id;
            Need = work.topic ?? work.name;
            projectPlate = StandForReopened(thing); // F105: one stand, under the work being edited
            endingLastProduct = thing;
            endingLastDesign = Design.Copy();
            Current = Step.Done;
            productFinished = finishDone = true;
            CloseNotebook();
            CloseQuestion("reopened work");
            work.completed = false;
            work.finalNameReady = false;
            work.pickupCode = work.pickupUrl = null;
            SaveNotebook();
            FinishActivity();
            Say(Crew.Maker, "Let's work on it again.");
        }
    }
}
