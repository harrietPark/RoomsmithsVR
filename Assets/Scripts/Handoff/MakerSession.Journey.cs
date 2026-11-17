using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;
using Oculus.Interaction.Input;
using Meta.XR.MRUtilityKit;

namespace Windups.Handoff
{
    public sealed partial class MakerSession
    {
        private bool restoringAtLaunch;
        private readonly Dictionary<Transform, string> savedProductIds = new();

        private string SavedProductId(Transform thing)
        {
            if (!savedProductIds.TryGetValue(thing, out var id))
                savedProductIds[thing] = id = Guid.NewGuid().ToString("N");
            return id;
        }

        private Made SavedItem(Memory memory, Transform thing, string name)
        {
            if (savedProductIds.TryGetValue(thing, out var id))
            {
                var item = memory.items.LastOrDefault(m => m.id == id);
                if (item != null) return item;
            }
            notebookOwners.TryGetValue(thing, out var owner);
            return memory.items.LastOrDefault(m => m.id == null && m.name == name &&
                (owner == null || owner == m.notebookId));
        }

        private void TrackRestoredWork(Made item, Transform thing)
        {
            restoredMade.Add(RestoreKey(item)); // F91: in the room now (a later Restore leaves it be)
            if (string.IsNullOrEmpty(item.id)) return;
            savedProductIds[thing] = item.id;
        }

        private void ShowNotebookJourney(NotebookWork work, int page)
        {
            var images = JourneyImages(work).ToArray();
            if (images.Length == 0) return;
            notebookWidgets.ForEach(widget => { if (widget != null) Destroy(widget); });
            notebookWidgets.Clear();
            notebookButtons.Clear();
            TextOnPage(0, work.name, 8, 5, 134, 25, 13);
            int pages = Mathf.CeilToInt(images.Length / 4f);
            TextOnPage(1, $"Journey  {page + 1} / {pages}", 8, 5, 134, 24, 12);
            for (int i = 0; i < 4 && page * 4 + i < images.Length; i++)
                PictureOnPage(i / 2, images[page * 4 + i], 8, 30 + (i % 2) * 63, 134, 60);
            BookButton(0, "Back to work", 8, 164, 91, FillSpread);
            BookButton(1, "<", 8, 164, 31, () => ShowNotebookJourney(work, page - 1), page > 0);
            BookButton(1, ">", 47, 164, 31, () => ShowNotebookJourney(work, page + 1), page + 1 < pages);
            BookButton(1, "x", 105, 164, 34, CloseNotebook);
        }

        private readonly Dictionary<Transform, Transform> finishedNoteArt = new();
        private readonly Dictionary<Transform, int> finishedNotePage = new();
        private const string NotebookPoseName = "maker_notebook_pose.json";

        private void WriteNotebookAtomic()
        {
            string temporary = NotebookPath + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(new NotebookData { notes = Notes, works = NotebookWorks }, Formatting.Indented));
            if (File.Exists(NotebookPath)) File.Replace(temporary, NotebookPath, null);
            else File.Move(temporary, NotebookPath);
        }

        public IEnumerator RestoreBeforeTitle()
        {
            if (!HasHistory) yield break;
            float until = Time.realtimeSinceStartup + 8f;
            while (MRUK.Instance?.GetCurrentRoom() == null && Time.realtimeSinceStartup < until) yield return null;
            if (MRUK.Instance?.GetCurrentRoom() == null) yield break;
            EnsureCrew();
            LoadNotes();
            restoringAtLaunch = true;
            try
            {
                Restore();
                PutUpWall();
                RestoreNotebookPose();
            }
            finally { restoringAtLaunch = false; }
        }

        private void RestoreNotebookPose()
        {
            var path = Path.Combine(Application.persistentDataPath, NotebookPoseName);
            if (!File.Exists(path)) return;
            try
            {
                var location = JsonConvert.DeserializeObject<Made>(File.ReadAllText(path));
                if (location == null || !PlaceFromAnchor(location.anchor, location.local, location.rotation, out var point, out var turn)) return;
                EnsureNotebookObjects();
                notebookView.SetPositionAndRotation(point, turn);
                notebookPlaced = true;
                notebookClosed.SetPositionAndRotation(point, turn);
                notebookClosed.gameObject.SetActive(true);
            }
            catch (Exception e) { Debug.LogWarning("[Roomsmiths] Notebook pose: " + e.Message); }
        }

        private bool notebookPlaced;
        private void RememberNotebookPose()
        {
            if (notebookView == null) return;
            var anchor = AnchorUnder(notebookView.position);
            if (anchor == null) return;
            var point = anchor.transform.InverseTransformPoint(notebookView.position);
            var turn = Quaternion.Inverse(anchor.transform.rotation) * notebookView.rotation;
            var location = new Made { anchor = AnchorKey(anchor), local = new[] { point.x, point.y, point.z }, rotation = new[] { turn.x, turn.y, turn.z, turn.w } };
            try
            {
                File.WriteAllText(Path.Combine(Application.persistentDataPath, NotebookPoseName), JsonConvert.SerializeObject(location));
                notebookPlaced = true;
                notebookClosed.SetPositionAndRotation(notebookView.position, notebookView.rotation);
            }
            catch (Exception e) { Debug.LogWarning("[Roomsmiths] Notebook pose: " + e.Message); }
        }

        private bool MoveAsideFromPlate(Transform thing, Transform incoming = null)
        {
            if (thing == null || projectPlate == null || !StandsOn(thing, projectPlate)) return true;
            if (held == thing) return false;
            var collider = thing.GetComponentsInChildren<Collider>().FirstOrDefault(c => c.enabled && !c.isTrigger);
            if (collider == null) return false;
            Bounds bounds = collider.bounds;
            var start = thing.position;
            var centerOffset = bounds.center - start;
            var half = bounds.extents * .97f;
            float radius = Mathf.Max(half.x, half.z);
            float first = StandRadiusOf(projectPlate) + radius + .025f;
            bool Clear(Vector3 point, bool leaveStand)
            {
                foreach (var hit in Physics.OverlapBox(point + centerOffset, half, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                    if ((incoming == null || !hit.transform.IsChildOf(incoming)) &&
                        (!leaveStand || !hit.transform.IsChildOf(projectPlate)) && Solid(hit, thing)) return false;
                return true;
            }
            for (int pass = 0; pass < 2; pass++)
                for (int ring = 0; ring < 5; ring++)
                    for (int direction = 0; direction < 12; direction++)
                    {
                        Vector3 at = projectPlate.position + Quaternion.Euler(0, direction * 30, 0) * Vector3.right * (first + ring * .08f);
                        if (pass == 0)
                        {
                            if (!DeskTop(at, out float y)) continue;
                            at.y = y + start.y - bounds.min.y + .002f;
                        }
                        else at.y = start.y + Mathf.Max(.15f, half.y);
                        if (!Clear(at, false)) continue;
                        var lift = new Vector3(start.x, Mathf.Max(start.y, at.y) + half.y + .025f, start.z);
                        var over = new Vector3(at.x, lift.y, at.z);
                        bool path = true;
                        for (int step = 1; step <= 8 && path; step++)
                        {
                            float t = step / 8f;
                            path = Clear(Vector3.Lerp(start, lift, t), true) && Clear(Vector3.Lerp(lift, over, t), false) &&
                                Clear(Vector3.Lerp(over, at, t), false);
                        }
                        if (!path) continue;
                        thing.position = at;
                        SyncBody(thing);
                        if (madeNames.TryGetValue(thing, out var name)) RememberPlace(name, thing);
                        return true;
                    }
            return false;
        }

        private bool SwitchPlateVersion(Version version)
        {
            var incoming = version.root;
            if (incoming == null || held != null || projectPlate == null) return false;
            if (Product != null && Product != incoming && !MoveAsideFromPlate(Product, incoming)) return false;
            if (!StandsOn(incoming, projectPlate))
            {
                var mesh = incoming.GetComponentInChildren<Renderer>();
                if (mesh == null) return false;
                var bounds = mesh.bounds;
                var top = StandTopOf(projectPlate);
                incoming.position += top - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                SyncBody(incoming);
            }
            Product = incoming;
            ProductMesh = incoming.GetComponentInChildren<MeshFilter>()?.sharedMesh;
            ProductBody = incoming.GetComponent<Rigidbody>();
            Design = version.design.Copy();
            productFinished = true;
            endingLastProduct = Product;
            endingLastDesign = Design.Copy();
            FinishActivity();
            Remember();
            return true;
        }

        private bool TrySelectPlateRider(Transform thing)
        {
            if (thing == null || projectPlate == null || thing == Product || !StandsOn(thing, projectPlate)) return false;
            if (PlatePagerOn) return false; // F102: with the stand's < > nothing is chosen by being put down there
            var version = Versions.FirstOrDefault(v => v.root == thing);
            if (version == null) return false;
            // F77: in the shape review the version put on the stand is the one worked on ("v1 it is!", its picture
            // and the shape choices); elsewhere it just becomes the current one.
            if (FormFirst && Review == ReviewStage.Shape && shapeRecords.ContainsKey(thing)) return SelectShapeVersion(version.number);
            if (!SwitchPlateVersion(version)) return false;
            Ack(Crew.Maker, "v" + version.number + " it is!");
            return true;
        }

        private IEnumerator FlyFinishedPhoto(Vector3 from, Transform book, string path)
        {
            if (book == null) yield break;
            var art = (RectTransform)new GameObject("Finished photo in flight", typeof(RectTransform), typeof(Canvas)).transform;
            art.SetParent(transform, false);
            art.localScale = Vector3.one * .001f;
            art.sizeDelta = new Vector2(95, 76);
            art.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            JourneyPicture(art, path, 0, 0, 95, 76);
            from += Vector3.up * .095f;
            try
            {
                for (float t = 0; t < 1f && book != null; t += Time.deltaTime / .65f)
                {
                    Vector3 destination = book.TransformPoint(new Vector3(-.065f, 0, -.018f));
                    Vector3 at = Vector3.Lerp(from, destination, Mathf.SmoothStep(0, 1, t)) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .065f;
                    art.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at));
                    yield return null;
                }
                if (book != null) ToySounds.Play(ToySounds.Cue.Paper, book.position, .6f, 1.45f);
            }
            finally { if (art != null) Destroy(art.gameObject); }
        }

        private IEnumerable<string> JourneyImages(NotebookWork work) =>
            Notes.Where(n => n.project == work.id && !string.IsNullOrEmpty(n.image)).Select(n => n.image)
                .Concat(new[] { work.reference, work.look, work.sketch }).Concat(work.finishedPhotos ?? new List<string>())
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path)).Distinct();

        private void RecordFinishedWall(NotebookWork work)
        {
            WallNote note = Wall.notes.LastOrDefault(n => n.projectId == work.id) ?? ringData;
            if (note == null)
            {
                RememberWallNote(work.topic ?? work.name);
                note = Wall.notes.LastOrDefault();
            }
            if (note == null) return;
            if (note.projectId != null && note.projectId != work.id)
            {
                note = new WallNote { text = work.topic ?? work.name, at = DateTime.Now.ToString("MMM d"), variant = "A" };
                Wall.notes.Add(note);
                if (board != null)
                {
                    wallViews.Add(WallView(note));
                    LayOutWall(false);
                }
            }
            note.projectId = work.id;
            SaveWall();
            PutUpWall();
            foreach (var pair in noteOf.ToArray())
                if (pair.Key != null && pair.Value == note) RefreshFinishedNote(pair.Key, note);
        }

        private void RefreshFinishedNote(Transform root, WallNote note)
        {
            if (string.IsNullOrEmpty(note.projectId)) return;
            LoadNotes();
            var work = NotebookWorks.FirstOrDefault(w => w.id == note.projectId);
            if (work == null) return;
            if (finishedNoteArt.TryGetValue(root, out var old) && old != null) Destroy(old.gameObject);
            var art = new GameObject("Creation journey", typeof(RectTransform), typeof(Canvas)).transform as RectTransform;
            art.SetParent(root, false);
            art.sizeDelta = new Vector2(72, 72);
            art.localScale = Vector3.one * .001f;
            art.localPosition = new Vector3(0, -.013f, -.006f);
            art.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            finishedNoteArt[root] = art;
            var words = root.Find("Words");
            var label = words != null ? words.GetComponentInChildren<TMPro.TMP_Text>() : null;
            if (JourneyPicture(art, work.photo, 0, 0, 37, 27) && label != null) label.rectTransform.sizeDelta = new Vector2(62, 21);
            foreach (var card in journeyCards.Where(c => c.Value.work == work))
                if (card.Key.root != null) card.Key.root.gameObject.SetActive(false);
            if (!finishedNotePage.TryGetValue(root, out int page) || page < 0) return;
            var images = JourneyImages(work).ToArray();
            var shown = images.Skip(page * 4).Take(4).ToArray();
            for (int i = 0; i < shown.Length; i++) ShowJourneyCard(work, shown[i], root, i);
            if (images.Length > 4) AddJourneyNext(art, page, images.Length);
        }

        private void AddJourneyNext(RectTransform parent, int page, int count)
        {
            var next = HatIdeaCard.Label(parent, font, 73f, 65f, 73f, 17f, 8f, 10f, Color.black,
                TMPro.FontStyles.Normal, TMPro.TextAlignmentOptions.Center);
            next.text = $"Next  {page + 1}/{Mathf.CeilToInt(count / 4f)}  >";
            next.raycastTarget = false;
        }

        private bool JourneyPicture(RectTransform parent, string path, float x, float y, float width, float height)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            // F104: the picture owns its texture (the notebook's refresh destroyed the shared one: a black square), and
            // a picture that did not load or shows nothing is left out rather than shown as a blank square.
            var texture = LoadOwnImage(path);
            if (texture == null) return false;
            var node = new GameObject("Journey photo", typeof(RectTransform), typeof(RawImage));
            OwnedTexture.Own(node, texture);
            var rect = (RectTransform)node.transform;
            rect.SetParent(parent, false);
            rect.anchoredPosition = new Vector2(x, y);
            float ratio = texture.width / (float)texture.height;
            rect.sizeDelta = ratio > width / height ? new Vector2(width, width / ratio) : new Vector2(height * ratio, height);
            node.GetComponent<RawImage>().texture = texture;
            node.GetComponent<RawImage>().raycastTarget = false;
            return true;
        }

        private readonly HashSet<Hand> finishedNoteTouches = new();
        private void TouchFinishedNotes()
        {
            foreach (var hand in HandSet.Both())
            {
                if (hand == null) continue;
                if (!Fingertip(hand, out var tip) || HeldBy(hand) != null || FinishedNoteHit(tip, null, out _) == null)
                    finishedNoteTouches.Remove(hand);
                else if (finishedNoteTouches.Add(hand)) ToggleFinishedNote(tip, null);
            }
        }

        private Transform FinishedNoteHit(Vector3 point, Ray? ray, out float distance)
        {
            distance = float.PositiveInfinity;
            Transform best = null;
            foreach (var pair in finishedNoteArt)
            {
                if (pair.Key == null || !pair.Key.gameObject.activeInHierarchy) continue;
                var root = pair.Key;
                Vector3 local;
                float d;
                if (ray.HasValue)
                {
                    if (!new Plane(root.forward, root.position).Raycast(ray.Value, out d)) continue;
                    local = root.InverseTransformPoint(ray.Value.GetPoint(d));
                }
                else
                {
                    local = root.InverseTransformPoint(point);
                    d = Mathf.Abs(local.z);
                    if (d > .026f) continue;
                }
                // The photo area opens the journey. Edges remain available for moving the note.
                bool photo = Mathf.Abs(local.x) <= .019f && local.y >= -.028f && local.y <= .003f;
                bool next = finishedNotePage.TryGetValue(root, out var page) && page >= 0 &&
                    local.x >= .035f && local.x <= .112f && local.y >= -.061f && local.y <= -.040f;
                if (d >= distance || (!photo && !next)) continue;
                best = root;
                distance = d;
            }
            return best;
        }

        private bool ToggleFinishedNote(Vector3 point, Ray? ray)
        {
            var root = FinishedNoteHit(point, ray, out _);
            if (root == null || !noteOf.TryGetValue(root, out var note)) return false;
            bool open = finishedNotePage.TryGetValue(root, out int page) && page >= 0;
            if (ray.HasValue && new Plane(root.forward, root.position).Raycast(ray.Value, out float span))
                point = ray.Value.GetPoint(span);
            var local = root.InverseTransformPoint(point);
            var work = NotebookWorks.FirstOrDefault(w => w.id == note.projectId);
            int pages = work == null ? 1 : Mathf.Max(1, Mathf.CeilToInt(JourneyImages(work).Count() / 4f));
            finishedNotePage[root] = open && local.x >= .035f ? (page + 1) % pages : open ? -1 : 0;
            RefreshFinishedNote(root, note);
            ToySounds.Play(ToySounds.Cue.Paper, root.position, .4f, 1.2f);
            return true;
        }
    }
}
