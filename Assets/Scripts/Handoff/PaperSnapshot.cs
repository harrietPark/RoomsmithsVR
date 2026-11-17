using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // A passive copy of something a character said: used for the history deck behind a
    // card, ideas kept in the ring, ideas handed to another character, and crumpled ideas
    // in Benefit's basket. It glides toward whatever pose it is given.
    public sealed class PaperSnapshot : MonoBehaviour
    {
        public const float DefaultWidth = .16f, DefaultHeight = .066f;
        // A copy can match the live card it was taken from (a kept idea stays card-sized).
        public float Width { get; private set; } = DefaultWidth;
        public float Height { get; private set; } = DefaultHeight;
        public string Speaker { get; private set; }
        public string Text { get; private set; }
        public List<string> Stickers { get; } = new List<string>();
        public bool IsBall { get; private set; }
        // The meeting line this copy came from (lets a card pulled out of a deck be handed over).
        public MeetingLine Line { get; set; }

        private static readonly Color Edge = new Color(.88f, .88f, .92f, 1f);
        private static readonly Color Rear = new Color(.95f, .95f, .97f, 1f);
        private readonly List<Mesh> meshes = new List<Mesh>();
        private Transform card, ball, stickerRoot;
        private GameObject textObject;
        private Vector3 targetPosition;
        private Quaternion targetRotation = Quaternion.identity;
        private float targetScale = 1f;
        private bool snapNext = true;

        public static PaperSnapshot Create(Transform parent, string speaker, string text, IEnumerable<string> stickers,
            Color roleColor, Material material, TMP_FontAsset font, float width = DefaultWidth, float height = DefaultHeight)
        {
            var go = new GameObject("Snapshot " + speaker);
            go.transform.SetParent(parent, false);
            go.layer = parent != null ? parent.gameObject.layer : 0;
            var snapshot = go.AddComponent<PaperSnapshot>();
            snapshot.Width = Mathf.Max(.1f, width);
            snapshot.Height = Mathf.Max(.05f, height);
            snapshot.Build(speaker, text, roleColor, material, font);
            if (stickers != null) foreach (var sticker in stickers) snapshot.AddSticker(sticker, material, font);
            return snapshot;
        }

        private void Build(string speaker, string text, Color roleColor, Material material, TMP_FontAsset font)
        {
            Speaker = speaker;
            Text = text;
            card = new GameObject("Card").transform;
            card.SetParent(transform, false);
            AddMesh(card, ToyMeshes.RoundedSlab(Width, Height, .006f, .005f, Color.white, Edge, Rear, "SnapshotCard"), Vector3.zero, material);
            bool light = roleColor.grayscale > .8f;
            AddMesh(card, ToyMeshes.Disc(.006f, .002f, roleColor, light ? new Color(.7f, .7f, .75f) : roleColor * .85f, "SnapshotDot"),
                new Vector3(-Width * .5f + .012f, Height * .5f - .013f, -.0012f), material);
            var canvasObject = new GameObject("Text", typeof(RectTransform), typeof(Canvas));
            canvasObject.layer = gameObject.layer;
            var rect = (RectTransform)canvasObject.transform;
            rect.SetParent(card, false);
            rect.sizeDelta = new Vector2(Width * 1000f - 30f, Height * 1000f - 10f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(.004f, 0f, -.0012f);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var label = new GameObject("Line", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            label.rectTransform.SetParent(rect, false);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
            if (font != null) label.font = font;
            label.text = text;
            label.color = new Color(.165f, .165f, .18f, 1f);
            label.fontStyle = FontStyles.Bold;
            label.enableAutoSizing = true;
            label.fontSizeMin = 8f;
            label.fontSizeMax = Mathf.Clamp(Height * 260f, 15f, 22f); // card-sized copies read like the card
            label.alignment = TextAlignmentOptions.Left;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
            textObject = label.gameObject;
            stickerRoot = new GameObject("Stickers").transform;
            stickerRoot.SetParent(card, false);

            ball = GameObject.CreatePrimitive(PrimitiveType.Sphere).transform;
            Destroy(ball.GetComponent<Collider>());
            ball.name = "Paper Ball";
            ball.SetParent(transform, false);
            ball.localScale = Vector3.one * .034f;
            ball.GetComponent<MeshFilter>().sharedMesh = CrumpledBall(ball.GetComponent<MeshFilter>().sharedMesh);
            var ballRenderer = ball.GetComponent<MeshRenderer>();
            ballRenderer.sharedMaterial = material;
            ballRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ball.gameObject.SetActive(false);
        }

        // Lumpy white paper ball (the toy shader reads vertex colours; the primitive has none).
        private Mesh CrumpledBall(Mesh source)
        {
            var mesh = Instantiate(source);
            mesh.name = "PaperBall";
            var vertices = mesh.vertices;
            var colors = new Color[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                // Hash the rounded position so seam duplicates move together.
                float h = Mathf.Abs(Mathf.Sin(Mathf.Round(v.x * 40f) * 12.9898f + Mathf.Round(v.y * 40f) * 78.233f + Mathf.Round(v.z * 40f) * 37.719f) * 43758.55f) % 1f;
                vertices[i] = v * (.86f + .26f * h);
                colors[i] = Color.Lerp(new Color(.9f, .9f, .93f), Color.white, h);
            }
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            meshes.Add(mesh);
            return mesh;
        }

        // What the copy shows (e.g. the short summary the card showed); Text keeps the full line.
        public void ShowText(string shown)
        {
            var label = textObject.GetComponent<TextMeshProUGUI>();
            if (label != null && !string.IsNullOrWhiteSpace(shown)) label.text = shown;
        }

        public void AddSticker(string kind, Material material, TMP_FontAsset font)
        {
            Stickers.Add(kind);
            int index = stickerRoot.childCount;
            var sticker = BubbleInteractions.MakeSticker(kind, stickerRoot, material, font, .5f);
            sticker.localPosition = new Vector3(Width * .5f - .008f - index * .016f, Height * .5f - .002f, -.004f);
            sticker.localRotation = Quaternion.Euler(0f, 0f, index % 2 == 0 ? -12f : 10f);
        }

        // See-through look for earlier cards stacked behind a held card.
        private static Material fadedMaterial;
        private readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
        private readonly List<Material> solid = new List<Material>();
        public bool Faded { get; private set; }
        public void SetFaded(bool faded)
        {
            if (Faded == faded) return;
            Faded = faded;
            if (fadedMaterial == null)
            {
                var source = Resources.Load<Material>("ToyFaded");
                if (source != null) { fadedMaterial = new Material(source); fadedMaterial.SetFloat("_Alpha", .62f); }
            }
            for (int i = 0; i < renderers.Count; i++)
                if (renderers[i] != null) renderers[i].sharedMaterial = faded && fadedMaterial != null ? fadedMaterial : solid[i];
            var label = textObject.GetComponent<TextMeshProUGUI>();
            if (label != null) label.alpha = faded ? .5f : 1f;
            foreach (var image in stickerRoot.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                image.color = new Color(1f, 1f, 1f, faded ? .5f : 1f);
        }

        private void AddMesh(Transform parent, Mesh mesh, Vector3 local, Material material)
        {
            meshes.Add(mesh);
            var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
            go.layer = gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderers.Add(renderer);
            solid.Add(material);
        }

        public void SetPose(Vector3 position, Quaternion rotation, float scale, bool showText, bool snap = false)
        {
            targetPosition = position;
            targetRotation = rotation;
            targetScale = scale;
            if (textObject.activeSelf != showText) textObject.SetActive(showText);
            if (stickerRoot.gameObject.activeSelf != showText) stickerRoot.gameObject.SetActive(showText);
            if (snap) snapNext = true;
        }

        // Crumpled into a paper ball (basket) or unfolded back into a card.
        public void SetBall(bool value)
        {
            IsBall = value;
            card.gameObject.SetActive(!value);
            ball.gameObject.SetActive(value);
        }

        public bool Raycast(Ray ray, out float distance)
        {
            distance = 0f;
            if (!gameObject.activeInHierarchy) return false;
            if (IsBall)
            {
                Vector3 toCenter = ball.position - ray.origin;
                distance = Vector3.Dot(toCenter, ray.direction);
                return distance > 0f && (toCenter - ray.direction * distance).sqrMagnitude < .03f * .03f;
            }
            var plane = new Plane(card.forward, card.position);
            if (!plane.Raycast(ray, out distance)) return false;
            return InBounds(ray.GetPoint(distance), .004f);
        }

        public bool Touches(Vector3 point)
        {
            if (!gameObject.activeInHierarchy) return false;
            if (IsBall) return Vector3.Distance(point, ball.position) < .035f;
            Vector3 local = card.InverseTransformPoint(point);
            return Mathf.Abs(local.z) < .02f && InBounds(point, .006f);
        }

        private bool InBounds(Vector3 point, float margin)
        {
            Vector3 local = card.InverseTransformPoint(point);
            return Mathf.Abs(local.x) <= Width * .5f + margin && Mathf.Abs(local.y) <= Height * .5f + margin;
        }

        private void LateUpdate()
        {
            float blend = snapNext ? 1f : 1f - Mathf.Exp(-Time.deltaTime * 9f);
            snapNext = false;
            transform.position = Vector3.Lerp(transform.position, targetPosition, blend);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, blend);
            transform.localScale = Vector3.one * Mathf.Lerp(transform.localScale.x, targetScale, blend);
        }

        private void OnDestroy()
        {
            foreach (var mesh in meshes) if (mesh != null) Destroy(mesh);
        }
    }
}
