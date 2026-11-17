using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    // F34: saved work spreads; closed follows Maker, opened pose belongs to the person.
    public sealed partial class MakerSession
    {
        [Serializable] public sealed class NotePage { public string project, kind, title, text, image, at; }
        [Serializable] private sealed class NotebookData { public List<NotePage> notes = new List<NotePage>(); public List<NotebookWork> works = new List<NotebookWork>(); }
        public const string NotebookFileName = "maker_notebook.json";
        public static string NotebookPath => Path.Combine(Application.persistentDataPath, NotebookFileName);
        private static string NotesFolder => Path.Combine(Application.persistentDataPath, "notes");

        public readonly List<NotePage> Notes = new List<NotePage>();
        public bool NotebookOpen { get; private set; }
        public int NotebookSpread { get; private set; }
        public int NotebookSpreads => IndexSpreads + NotebookWorks.Count;
        public Transform NotebookBook => notebookClosed;
        public Transform NotebookView => notebookView;

        private bool notesLoaded;
        private string project;
        private Transform notebookClosed, notebookView;
        
        private readonly List<Texture2D> noteTextures = new List<Texture2D>();
        private const float PageW = .15f, PageH = .19f;

        private void LoadNotes()
        {
            if (notesLoaded) return;
            notesLoaded = true;
            try
            {
                if (File.Exists(NotebookPath))
                    {
 var data = JsonConvert.DeserializeObject<NotebookData>(File.ReadAllText(NotebookPath)) ?? new NotebookData();
 Notes.AddRange(data.notes ?? new List<NotePage>());
 NotebookWorks.AddRange(data.works ?? new List<NotebookWork>());
}
            }
            catch (Exception exception) { Debug.LogWarning("[Windups Notebook] " + exception.Message); }
        }

        private void NewProject() => project = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff");

        // A line in Maker's notebook (with an optional picture).
        private void Note(string kind, string title, string text, byte[] jpeg = null)
        {
            LoadNotes();
            if (project == null) NewProject();
            string image = null;
            if (jpeg != null && jpeg.Length > 0)
                try
                {
                    Directory.CreateDirectory(NotesFolder);
                    image = Path.Combine(NotesFolder, project + "_" + Guid.NewGuid().ToString("N") + ".jpg");
                    File.WriteAllBytes(image, jpeg);
                }
                catch (Exception exception) { image = null; Debug.LogWarning("[Windups Notebook] " + exception.Message); }
            // One line per kind and title within a build: a newer decision replaces the older one.
            Notes.RemoveAll(n => n.project == project && n.kind == kind && n.title == title && kind != "decision" && jpeg == null);
            Notes.Add(new NotePage { project = project, kind = kind, title = MakerRoles.Clean(title), text = MakerRoles.Clean(text ?? ""), image = image, at = DateTime.Now.ToString("MMM d, HH:mm") });
            try { WriteNotebookAtomic(); }
            catch (Exception exception) { Debug.LogWarning("[Windups Notebook] " + exception.Message); }
            if (notebookClosed != null && notebookClosed.gameObject.activeInHierarchy)
            {
                ToyFx.Puff(notebookClosed.position + Vector3.up * .01f, .006f, 2);
                ToySounds.Play(ToySounds.Cue.Paper, notebookClosed.position, .22f, 2.1f);
                StartCoroutine(Bump(notebookClosed));
            }
            if (Current == Step.Done && Product != null) QueueNotebookSnapshot(Product);
            if (NotebookOpen) FillSpread();
        }

        public static void ForgetNotes()
        {
            try { if (File.Exists(NotebookPath)) File.Delete(NotebookPath); } catch { }
            try { if (Directory.Exists(NotesFolder)) Directory.Delete(NotesFolder, true); } catch { }
        }

        // ------------------------------------------------------------ pages

        // ------------------------------------------------------------ open, flip, close

        public void OpenNotebook()
        {
            if (!Begun || NotebookOpen) return;
            EnsureNotebookObjects();
            NotebookOpen = true;
            NotebookSpread = 0;
            Heard();
            Body(Crew.Maker)?.Play("Decision_Present");
            Say(Crew.Maker, "Our makes. Pick one to see what went into it.");
            Vector3 home = FrontPoint(.42f, -.17f, out _);
            if (!notebookPlaced) notebookView.SetPositionAndRotation(home, SpeechBubbleDock.FacingAt(home));
            notebookView.localScale = Vector3.one;
            notebookView.gameObject.SetActive(true);

            FillSpread();
            ToySounds.Play(ToySounds.Cue.Paper, home, .6f, 1.1f);
            // Keep grip geometry stable from the first frame (no scale animation fighting a grab).
        }

        public void CloseNotebook()
        {
            if (!NotebookOpen) return;
            EndNotebookGrab();
            NotebookOpen = false;
            Heard();
            ToySounds.Play(ToySounds.Cue.Paper, notebookView.position, .45f, .9f);
            ToyFx.Puff(notebookView.position, .012f, 3);
            notebookView.gameObject.SetActive(false);
        }

        public void FlipNotebook(int by)
        {
            if (!NotebookOpen) return;
            int next = Mathf.Clamp(NotebookSpread + by, 0, NotebookSpreads - 1);
            if (next == NotebookSpread) return;
            NotebookSpread = next;
            ToySounds.Play(ToySounds.Cue.Paper, notebookView.position, .4f, 1.6f);
            FillSpread();
        }


        private bool NotebookVoice(string p)
        {
            p = p.Trim().TrimEnd('.', '!', '?');
            if (NotebookOpen && (p == "close" || p == "close the notebook" || p == "close the notes" || p == "회의록 닫아")) { CloseNotebook(); return true; }
            if (NotebookOpen && (p == "next page" || p == "다음 페이지")) { FlipNotebook(1); return true; }
            if (NotebookOpen && (p == "previous page" || p == "이전 페이지")) { FlipNotebook(-1); return true; }
            if (p == "show the notes" || p == "show me the notes" || p == "open the notebook" || p == "show the notebook" || p == "회의록 보여줘" || p == "회의록 열어줘")
            { OpenNotebook(); return true; }
            return false;
        }

        private bool NotebookPinch(Vector3 point, Ray? ray, Oculus.Interaction.Input.Hand hand = null)
        {
            if (!Begun || notebookClosed == null) return false;
            // (2026-11-12) The closed notebook lies by Maker on the desk where the hands work: a pinch whose ray
            // merely passed near it used to open it (and the open book then took the tools' pinches). From afar
            // only a pinch aimed right at it opens it, and never while the palette is up (that pinch picks a colour).
            bool picking = PaletteOnPalm && hand != null && hand.Handedness == Oculus.Interaction.Input.Handedness.Right;
            if (!NotebookOpen)
            {
                if (!notebookClosed.gameObject.activeInHierarchy) return false;
                bool near = Vector3.Distance(point, notebookClosed.position) < .06f;
                bool aimed = ray.HasValue && RayDistance(ray.Value, notebookClosed.position) < .02f && !picking;
                if (!near && !aimed) return false;
                OpenNotebook(); return true;
            }
            if (NotebookHeld) return hand == null || hand == notebookHand;
            // A colour pick through the open book (it stands between the eyes and the desk) is the palette's.
            if (picking && Vector3.Distance(point, notebookView.position) > .12f) return false;
            Vector3 local = notebookView.InverseTransformPoint(point);
            float distance = 0f;
            if (float.IsInfinity(local.x) || float.IsNaN(local.x) || Mathf.Abs(local.z) > .035f || Mathf.Abs(local.x) > .17f || Mathf.Abs(local.y) > .11f)
            {
                if (!ray.HasValue || !NotebookOnRay(ray.Value, out distance)) return false;
                local = notebookView.InverseTransformPoint(ray.Value.GetPoint(distance));
            }
            if (Mathf.Abs(local.x) > .145f || Mathf.Abs(local.y) > .093f)
            {
                // Near hands use HandGrabInteractable. Far/mouse use the same SDK pointer events.
                if (hand == null || distance > RemoteGrab.NearLimit)
                    BeginNotebookGrab(hand, ray, point, distance);
                return true;
            }
            foreach (var button in notebookButtons)
                if (button.rect.Contains(new Vector2(local.x, local.y))) { button.click(); break; }
            return true;
        }

        private bool NotebookOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            if (!Begun || notebookClosed == null) return false;
            if (NotebookOpen)
            {
                if (!new Plane(notebookView.forward, notebookView.position).Raycast(ray, out float d) || d <= 0f) return false;
                Vector3 l = notebookView.InverseTransformPoint(ray.GetPoint(d));
                distance = d;
                return Mathf.Abs(l.x) < PageW + .02f && Mathf.Abs(l.y) < PageH * .6f;
            }
            if (!notebookClosed.gameObject.activeInHierarchy || RayDistance(ray, notebookClosed.position) > .035f) return false;
            distance = Vector3.Distance(ray.origin, notebookClosed.position);
            return true;
        }

        // ------------------------------------------------------------ every frame

        public static float ClosedBookFollow = .04f; // metres Maker must move before its closed notebook is put down again
        public int ClosedBookMoves { get; private set; }
        private Vector3 closedBookAnchor;
        private Pose closedBookPose;
        private bool closedBookWasShown;

        private void NotebookFrame(float blend)
        {
            if (!Begun) return;
            EnsureSolidTicker(); // F57/F59 and the 2026-11-13 polish (name tags, colour wheel) tick from the session's start
            EnsureNotebookObjects();
            // The closed book lies on the desk beside Maker (not while Maker sketches in it, or while open).
            bool show = !NotebookOpen && !Sketching && cast.TryGetValue(Crew.Maker, out var maker) && maker != null;
            if (notebookClosed.gameObject.activeSelf != show) notebookClosed.gameObject.SetActive(show);
            if (show && cast.TryGetValue(Crew.Maker, out var m))
            {
                // (2026-11-13, the user: "Maker's notebook still follows me") It used to be placed every frame from where
                // the person stood, so it slid and turned on the desk as they moved. Now it is put down once beside Maker
                // and stays (world-locked); only Maker moving more than ClosedBookFollow puts it down again.
                Vector3 at = m.transform.position;
                bool shownNow = !closedBookWasShown;
                if (shownNow || Vector3.Distance(at, closedBookAnchor) > ClosedBookFollow)
                {
                    Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - at, Vector3.up) : Vector3.back;
                    toUser = toUser.sqrMagnitude > .0001f ? toUser.normalized : Vector3.back;
                    Vector3 side = SketchSide(m, toUser);
                    closedBookPose = new Pose(at + side * .085f + toUser * .03f + Vector3.up * .006f,
                        Quaternion.LookRotation(-(Vector3.up + toUser * .35f).normalized, -toUser));
                    closedBookAnchor = at;
                    ClosedBookMoves++;
                    if (shownNow && !notebookPlaced) notebookClosed.SetPositionAndRotation(closedBookPose.position, closedBookPose.rotation);
                }
                if (!notebookPlaced) notebookClosed.SetPositionAndRotation(Vector3.Lerp(notebookClosed.position, closedBookPose.position, blend), Quaternion.Slerp(notebookClosed.rotation, closedBookPose.rotation, blend));
            }
            closedBookWasShown = show;
            if (NotebookOpen)
            {
                // Open pose belongs only to Grabbable, never to the viewer.
                NotebookButtonFrame();
                foreach (var pair in cast) if (pair.Value != null) pair.Value.lookTarget = notebookView;
            }
        }

        // ------------------------------------------------------------ objects

        private TextMeshProUGUI[] pageTitles, pageBodies, pageFooters;
        private RawImage[] pageImages;

        private void EnsureNotebookObjects()
        {
            if (notebookClosed != null) return;
            LoadNotes();
            Color cover = MakerRoles.Color(Crew.Maker), coverEdge = Color.Lerp(cover, Color.black, .3f), paper = new Color(1f, .99f, .96f);
            // Closed: a blue spiral book with a cream page edge.
            notebookClosed = new GameObject("Maker Notebook").transform;
            notebookClosed.SetParent(transform, false);
            var coverMesh = ToyMeshes.RoundedSlab(.05f, .064f, .004f, .007f, cover, coverEdge, cover, "NotebookCover");
            owned.Add(coverMesh);
            Part(notebookClosed, "Cover", coverMesh, Vector3.zero, 0f);
            var edgeMesh = ToyMeshes.RoundedSlab(.046f, .06f, .003f, .002f, paper, new Color(.9f, .88f, .82f), paper, "NotebookPages");
            owned.Add(edgeMesh);
            Part(notebookClosed, "Pages", edgeMesh, new Vector3(.0015f, -.0015f, .0075f), 0f);
            var labelMesh = ToyMeshes.RoundedSlab(.03f, .012f, .003f, .001f, paper, paper, paper, "NotebookLabel");
            owned.Add(labelMesh);
            Part(notebookClosed, "Label", labelMesh, new Vector3(0f, .006f, -.0012f), 0f);
            var ring = ToyMeshes.Disc(.0024f, .0011f, new Color(.72f, .74f, .78f), new Color(.55f, .57f, .6f), "NotebookRing");
            owned.Add(ring);
            for (int i = 0; i < 6; i++)
            {
                var r = Part("Spiral", ring);
                r.SetParent(notebookClosed, false);
                r.localPosition = new Vector3(-.0205f + i * .0082f, .031f, -.0012f);
                r.localRotation = Quaternion.Euler(0f, 90f, 0f);
            }
            notebookClosed.gameObject.SetActive(false);

            // Open: a spread of two pages on a blue cover, spiral in the middle.
            notebookView = Instantiate(Resources.Load<GameObject>("MakerNotebookGrip"), transform).transform;
            notebookView.name = "Maker Notebook Open";
            notebookGrab = notebookView.GetComponent<Oculus.Interaction.Grabbable>();
            notebookView.SetParent(transform, false);
            var back = ToyMeshes.RoundedSlab(PageW * 2f + .022f, PageH + .016f, .012f, .006f, cover, coverEdge, cover, "NotebookBack");
            owned.Add(back);
            Part(notebookView, "Cover", back, new Vector3(0f, 0f, .004f), 0f);
            var pageMesh2 = ToyMeshes.RoundedSlab(PageW, PageH, .006f, .002f, paper, new Color(.88f, .86f, .8f), paper, "NotebookPage");
            owned.Add(pageMesh2);
            pageTitles = new TextMeshProUGUI[2];
            pageBodies = new TextMeshProUGUI[2];
            pageFooters = new TextMeshProUGUI[2];
            pageImages = new RawImage[2];
            for (int i = 0; i < 2; i++)
            {
                // Page 0 is on the person's left (the view faces them like the picture cards: local +x is their right).
                float x = (i == 0 ? -1f : 1f) * (PageW * .5f + .003f);
                Part(notebookView, "Page " + i, pageMesh2, new Vector3(x, 0f, 0f), 0f);
                var canvas = new GameObject("Page Text " + i, typeof(RectTransform), typeof(Canvas));
                var rect = (RectTransform)canvas.transform;
                rect.SetParent(notebookView, false);
                rect.sizeDelta = new Vector2(PageW * 1000f, PageH * 1000f);
                rect.localScale = Vector3.one * .001f;
                rect.localPosition = new Vector3(x, 0f, -.0012f);
                canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                var ink = new Color(.16f, .17f, .22f);
                pageTitles[i] = HatIdeaCard.Label(rect, font, 10f, 8f, PageW * 1000f - 20f, 22f, 12f, 17f, MakerRoles.Color(Crew.Maker) * .8f, FontStyles.Bold, TextAlignmentOptions.TopLeft);
                var image = new GameObject("Picture", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
                image.rectTransform.SetParent(rect, false);
                image.rectTransform.anchorMin = image.rectTransform.anchorMax = new Vector2(.5f, 1f);
                image.rectTransform.pivot = new Vector2(.5f, 1f);
                image.rectTransform.anchoredPosition = new Vector2(0f, -34f);
                image.rectTransform.sizeDelta = new Vector2(78f, 78f);
                image.raycastTarget = false;
                pageImages[i] = image;
                pageBodies[i] = HatIdeaCard.Label(rect, font, 10f, 34f, PageW * 1000f - 20f, PageH * 1000f - 52f, 6.5f, 9.5f, ink, FontStyles.Normal, TextAlignmentOptions.TopLeft);
                pageBodies[i].richText = true;
                pageFooters[i] = HatIdeaCard.Label(rect, font, 10f, PageH * 1000f - 16f, PageW * 1000f - 20f, 12f, 6f, 7f, new Color(.5f, .5f, .55f), FontStyles.Normal, i == 0 ? TextAlignmentOptions.BottomLeft : TextAlignmentOptions.BottomRight);
            }
            var ringBig = ToyMeshes.Disc(.004f, .002f, new Color(.72f, .74f, .78f), new Color(.55f, .57f, .6f), "NotebookRingBig");
            owned.Add(ringBig);
            for (int i = 0; i < 9; i++)
            {
                var r = Part("Spiral", ringBig);
                r.SetParent(notebookView, false);
                r.localPosition = new Vector3(0f, -PageH * .44f + i * PageH * .11f, -.002f);
                r.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            notebookView.gameObject.SetActive(false);
        }

        private Texture2D LoadNoteImage(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var texture = new Texture2D(2, 2) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
                texture.LoadImage(File.ReadAllBytes(path));
                noteTextures.Add(texture);
                return texture;
            }
            catch { return null; }
        }

        // F104: a picture for an object that keeps its own texture (OwnedTexture), never one the notebook's refresh
        // destroys. A file that does not load, or a photo with nothing in it (one colour all over: a render that showed
        // nothing), gives null, so no black or empty square is shown.
        public static int BlankPhotosSkipped { get; private set; }
        private static Texture2D LoadOwnImage(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var texture = new Texture2D(2, 2) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
                if (!texture.LoadImage(File.ReadAllBytes(path)) || texture.width < 8 || PictureBlank(texture))
                {
                    BlankPhotosSkipped++;
                    Debug.LogWarning("[Windups Notebook] picture left out (no image in it): " + Path.GetFileName(path));
                    Destroy(texture);
                    return null;
                }
                return texture;
            }
            catch { return null; }
        }

        // 20 x 20 samples: blank when hardly any differs from the corner (the background, or black all over).
        private static bool PictureBlank(Texture2D texture)
        {
            Color corner = texture.GetPixelBilinear(.01f, .01f);
            int differ = 0;
            for (int y = 0; y < 20; y++)
                for (int x = 0; x < 20; x++)
                {
                    Color c = texture.GetPixelBilinear((x + .5f) / 20f, (y + .5f) / 20f);
                    if (Mathf.Abs(c.r - corner.r) + Mathf.Abs(c.g - corner.g) + Mathf.Abs(c.b - corner.b) > .06f) differ++;
                }
            return differ < 2 || (corner.maxColorComponent < .03f && differ < 40);
        }

        // The same test on a photo's pixels before it is written (run inside the writing Task).
        public static bool PhotoBlank(Color32[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length < width * height || width < 8) return true;
            Color32 corner = pixels[0];
            int differ = 0, samples = 0;
            for (int y = 0; y < height; y += height / 40)
                for (int x = 0; x < width; x += width / 40)
                {
                    var c = pixels[y * width + x];
                    samples++;
                    if (Mathf.Abs(c.r - corner.r) + Mathf.Abs(c.g - corner.g) + Mathf.Abs(c.b - corner.b) > 15) differ++;
                }
            return differ < 3 || (corner.r + corner.g + corner.b < 24 && differ < samples / 10);
        }
    }
}
