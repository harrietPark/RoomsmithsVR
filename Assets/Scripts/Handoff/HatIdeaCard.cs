using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F24: a big, readable idea card in the Six Hats session. Title, one-line detail and one
    // short note per hat. The notes sit in the same order on every card, so two or three
    // ideas on the table can be compared row by row ("Risks" next to "Risks").
    public sealed class HatIdeaCard : MonoBehaviour
    {
        public const float Width = .2f, Height = .21f;
        private const float DropRadius = .016f;
        private static readonly Color Paper = new Color(1f, .995f, .975f), Edge = new Color(.86f, .86f, .9f), Rear = new Color(.94f, .94f, .96f);
        private static readonly Color InkDark = new Color(.16f, .16f, .18f), InkSoft = new Color(.42f, .42f, .46f);
        public static readonly Color MineColor = new Color(.56f, .42f, .86f);

        public HatIdea Idea { get; private set; }
        public bool Mine { get; private set; }
        public bool Kept { get; private set; }
        public bool Best { get; private set; }
        public bool Hovered { get; set; }
        public IReadOnlyDictionary<Hat, string> Notes => notes;
        public bool IsWaiting(Hat hat) => waiting.Contains(hat);
        public string NotesText => notesText != null ? notesText.text : "";
        public Vector3 DropPoint => transform.TransformPoint(new Vector3(Width * .5f - .018f, Height * .5f - .018f, -.002f));

        private readonly Dictionary<Hat, string> notes = new Dictionary<Hat, string>();
        private readonly HashSet<Hat> waiting = new HashSet<Hat>();
        private readonly List<Mesh> meshes = new List<Mesh>();
        private TextMeshProUGUI titleText, detailText, notesText, sourceText;
        private GameObject dropButton, bestBadge;
        private Mesh outlineMesh;
        private MeshRenderer outlineRenderer;
        private IList<Hat> order;
        private Vector3 targetPosition;
        private Quaternion targetRotation = Quaternion.identity;
        private float targetScale = 1f, shownScale, flashUntil, bornAt;
        private Color flashColor, outlineColor = Color.white;
        private bool moved;

        public static HatIdeaCard Create(Transform parent, HatIdea idea, bool mine, Material material, TMP_FontAsset font, IList<Hat> order)
        {
            var go = new GameObject("Idea " + idea.id);
            go.transform.SetParent(parent, false);
            var card = go.AddComponent<HatIdeaCard>();
            card.Idea = idea;
            card.Mine = mine;
            card.order = order;
            card.Build(material, font);
            card.bornAt = Time.time;
            return card;
        }

        private void Build(Material material, TMP_FontAsset font)
        {
            // Outline behind the card: blue when it is the strongest, a hat colour as a note lands.
            outlineMesh = ToyMeshes.RoundedSlab(Width + .012f, Height + .012f, .014f, .003f, Color.white, Color.white, Color.white, "IdeaOutline");
            outlineRenderer = AddMesh(transform, outlineMesh, new Vector3(0f, 0f, .0022f), material).GetComponent<MeshRenderer>();
            outlineRenderer.enabled = false;
            AddMesh(transform, ToyMeshes.RoundedSlab(Width, Height, .01f, .005f, Paper, Edge, Rear, "IdeaCard"), Vector3.zero, material);
            Color source = Mine ? MineColor : SixHats.Color(Hat.Green);
            AddMesh(transform, ToyMeshes.Disc(.0045f, .0015f, source, source * .85f, "IdeaSourceDot"),
                new Vector3(-Width * .5f + .014f, Height * .5f - .016f, -.0012f), material);

            var canvas = new GameObject("Text", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(transform, false);
            rect.sizeDelta = new Vector2(Width * 1000f, Height * 1000f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, 0f, -.0013f);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            // Canvas pixels are millimetres; y measured down from the top edge.
            sourceText = Label(rect, font, 22f, 8f, Width * 1000f - 60f, 14f, 10.5f, 10.5f, InkSoft, FontStyles.Normal, TextAlignmentOptions.Left);
            sourceText.text = Mine ? "My idea" : "New idea";
            titleText = Label(rect, font, 11f, 24f, Width * 1000f - 22f, 46f, 14f, 20f, InkDark, FontStyles.Bold, TextAlignmentOptions.TopLeft);
            detailText = Label(rect, font, 11f, 71f, Width * 1000f - 22f, 34f, 10f, 13f, InkSoft, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            notesText = Label(rect, font, 11f, 110f, Width * 1000f - 22f, Height * 1000f - 118f, 8.5f, 12.5f, InkDark, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            notesText.lineSpacing = -6f;
            notesText.paragraphSpacing = 4f;
            titleText.text = Idea.title;
            detailText.text = Idea.detail ?? "";
            // A thin rule between the idea and the hat notes.
            AddMesh(transform, ToyMeshes.RoundedSlab(Width - .024f, .0012f, .0005f, .0008f, Edge, Edge, Edge, "IdeaRule"),
                new Vector3(0f, Height * .5f - .106f, -.0011f), material);

            // Drop (x) in the top-right corner.
            dropButton = new GameObject("Drop");
            dropButton.transform.SetParent(transform, false);
            dropButton.transform.localPosition = new Vector3(Width * .5f - .018f, Height * .5f - .018f, -.0012f);
            AddMesh(dropButton.transform, ToyMeshes.Disc(.0095f, .0015f, new Color(.93f, .93f, .95f), new Color(.8f, .8f, .84f), "IdeaDrop"), Vector3.zero, material);
            var cross = new Color(.5f, .5f, .55f);
            for (int i = 0; i < 2; i++)
            {
                var bar = AddMesh(dropButton.transform, ToyMeshes.RoundedSlab(.011f, .0022f, .001f, .001f, cross, cross, cross, "IdeaDropBar"),
                    new Vector3(0f, 0f, -.0004f), material);
                bar.localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 45f : -45f);
            }

            // "Strongest" badge on the top edge (set by the blue hat).
            bestBadge = new GameObject("Strongest");
            bestBadge.transform.SetParent(transform, false);
            bestBadge.transform.localPosition = new Vector3(0f, Height * .5f + .002f, -.0016f);
            var blue = SixHats.Color(Hat.Blue);
            AddMesh(bestBadge.transform, ToyMeshes.RoundedSlab(.074f, .02f, .009f, .003f, blue, blue * .85f, blue, "IdeaBest"), Vector3.zero, material);
            var badgeCanvas = new GameObject("Text", typeof(RectTransform), typeof(Canvas));
            var badgeRect = (RectTransform)badgeCanvas.transform;
            badgeRect.SetParent(bestBadge.transform, false);
            badgeRect.sizeDelta = new Vector2(74f, 20f);
            badgeRect.localScale = Vector3.one * .001f;
            badgeRect.localPosition = new Vector3(0f, 0f, -.0004f);
            badgeCanvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var badge = Label(badgeRect, font, 0f, 0f, 74f, 20f, 11f, 11f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);
            badge.text = "Strongest";
            bestBadge.SetActive(false);
            Refresh();
        }

        internal static TextMeshProUGUI Label(RectTransform parent, TMP_FontAsset font, float x, float y, float width, float height,
            float minSize, float maxSize, Color color, FontStyles style, TextAlignmentOptions alignment)
        {
            var label = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            var r = label.rectTransform;
            r.SetParent(parent, false);
            r.anchorMin = r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(x, -y);
            r.sizeDelta = new Vector2(width, height);
            if (font != null) label.font = font;
            label.color = color;
            label.fontStyle = style;
            label.enableAutoSizing = minSize < maxSize;
            label.fontSizeMin = minSize;
            label.fontSizeMax = maxSize;
            label.fontSize = maxSize;
            label.alignment = alignment;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            return label;
        }

        private Transform AddMesh(Transform parent, Mesh mesh, Vector3 position, Material material)
        {
            meshes.Add(mesh);
            var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go.transform;
        }

        // ------------------------------------------------------------ state

        public void SetNote(Hat hat, string text)
        {
            waiting.Remove(hat);
            notes[hat] = text.Trim();
            Flash(SixHats.Ink(hat));
            Refresh();
        }

        public void SetWaiting(Hat hat, bool value)
        {
            if (value) waiting.Add(hat); else waiting.Remove(hat);
            Refresh();
        }

        public void SetBest(bool value)
        {
            Best = value;
            if (bestBadge != null) bestBadge.SetActive(value && !Kept);
            if (value) Flash(SixHats.Color(Hat.Blue));
            UpdateOutline();
        }

        public void SetKept(bool value)
        {
            Kept = value;
            if (dropButton != null) dropButton.SetActive(!value);
            if (bestBadge != null) bestBadge.SetActive(Best && !value);
            if (sourceText != null) sourceText.text = value ? "Kept" : Mine ? "My idea" : "New idea";
        }

        public void Flash(Color color)
        {
            flashColor = color;
            flashUntil = Time.time + .7f;
            UpdateOutline();
        }

        private void UpdateOutline()
        {
            if (outlineRenderer == null) return;
            bool flashing = Time.time < flashUntil;
            bool show = flashing || Best;
            if (outlineRenderer.enabled != show) outlineRenderer.enabled = show;
            if (!show) return;
            Color color = flashing ? flashColor : SixHats.Color(Hat.Blue);
            if (color == outlineColor) return;
            outlineColor = color;
            var colors = new Color[outlineMesh.vertexCount];
            for (int i = 0; i < colors.Length; i++) colors[i] = color;
            outlineMesh.colors = colors;
        }

        // One line per hat, in the session's order (the same on every card).
        public void Refresh()
        {
            if (notesText == null) return;
            var text = new StringBuilder();
            if (order != null)
                foreach (var hat in order)
                {
                    bool has = notes.TryGetValue(hat, out string note);
                    if (!has && !waiting.Contains(hat)) continue;
                    if (text.Length > 0) text.Append('\n');
                    text.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(SixHats.Ink(hat))).Append("><b>")
                        .Append(SixHats.Verb(hat)).Append("</b></color>  ");
                    text.Append(waiting.Contains(hat) ? "<color=#A0A0A8>...</color>" : note);
                }
            notesText.text = text.ToString();
        }

        // ------------------------------------------------------------ motion

        public void MoveTo(Vector3 position, Quaternion rotation, float scale, bool snap = false)
        {
            targetPosition = position;
            targetRotation = rotation;
            targetScale = scale;
            if (snap || !moved)
            {
                moved = true;
                if (snap) { transform.SetPositionAndRotation(position, rotation); shownScale = scale; transform.localScale = Vector3.one * scale; }
            }
        }

        public void StartFrom(Vector3 position)
        {
            moved = true;
            transform.position = position;
            shownScale = .15f;
            transform.localScale = Vector3.one * shownScale;
        }

        private void Update()
        {
            float blend = 1f - Mathf.Exp(-Time.deltaTime * 9f);
            transform.SetPositionAndRotation(Vector3.Lerp(transform.position, targetPosition, blend),
                Quaternion.Slerp(transform.rotation, targetRotation, blend));
            float hover = Hovered ? 1.035f : 1f;
            float flash = Time.time < flashUntil ? 1f + .03f * Mathf.Sin((flashUntil - Time.time) / .7f * Mathf.PI) : 1f;
            shownScale = Mathf.Lerp(shownScale, targetScale, 1f - Mathf.Exp(-Time.deltaTime * 10f));
            transform.localScale = Vector3.one * Mathf.Max(.01f, shownScale * hover * flash);
            UpdateOutline();
        }

        // ------------------------------------------------------------ hits

        public bool Raycast(Ray ray, out float distance, out bool onDrop)
        {
            onDrop = false;
            var plane = new Plane(transform.forward, transform.position);
            if (!plane.Raycast(ray, out distance) || distance <= 0f) return false;
            Vector3 local = transform.InverseTransformPoint(ray.GetPoint(distance));
            if (Mathf.Abs(local.x) > Width * .5f || Mathf.Abs(local.y) > Height * .5f + (Best ? .012f : 0f)) return false;
            onDrop = OnDrop(local);
            return true;
        }

        // A fingertip pressing the front of the card.
        public bool Touches(Vector3 point, out bool onDrop)
        {
            onDrop = false;
            Vector3 local = transform.InverseTransformPoint(point);
            float front = Vector3.Dot(point - transform.position, -transform.forward);
            if (front < -.006f || front > .016f) return false;
            if (Mathf.Abs(local.x) > Width * .5f || Mathf.Abs(local.y) > Height * .5f) return false;
            onDrop = OnDrop(local);
            return true;
        }

        private bool OnDrop(Vector3 local) =>
            !Kept && new Vector2(local.x - (Width * .5f - .018f), local.y - (Height * .5f - .018f)).magnitude < DropRadius;

        public Bounds WorldBounds => new Bounds(transform.position, Vector3.one * Width * transform.localScale.x);

        private void OnDestroy()
        {
            foreach (var mesh in meshes) if (mesh != null) Destroy(mesh);
        }
    }
}
