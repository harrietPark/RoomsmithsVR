using System;
using UnityEngine;
using UnityEngine.UI;
using Windups.BubbleLab;

namespace Windups.Handoff
{
    [DefaultExecutionOrder(150)]
    public sealed class SpeechBubblePresentation : MonoBehaviour
    {
        public Image badge;
        public Text badgeLabel;
        public MeshFilter pointerMeshFilter;
        public MeshRenderer pointerRenderer;
        public Transform viewerTarget;
        public string authorName = "Information";
        public float pointerLength = .068f;
        public float pointerHalfWidth = .031f;
        public float pointerVisibleExtension = .046f;
        // Main-scene instances opt in; the BubbleLab prefab default keeps the paper look.
        public bool toyStyle;
        public float popDuration = .42f;
        // Crisp SDF text for toy cards; null keeps the legacy Text rendering.
        public TMPro.TMP_FontAsset toyFont;
        public float thinkingScale = .42f;
        // Toy cards fit their text: a readable fixed size, never truncated ("..."),
        // shrinking only for very long lines. Units are card canvas units (1 = 1 mm).
        [NonSerialized] public float cardFontSize = 15f, guideFontSize = 23f, cardMinFontSize = 12f;
        [NonSerialized] public float cardMaxTextWidth = 118f, guideMaxTextWidth = 215f, cardMaxTextHeight = 105f;
        [NonSerialized] public float topicNoteFontSize = 30f;
        public static readonly Vector2 ThinkingPillSize = new Vector2(.052f, .03f);
        // In the hand a card becomes a BubbleLab-sized sheet so both hands fit on it: the summary
        // when compact, the whole sentence once stretched open (or brought close to read).
        [NonSerialized] public float heldFontSize = 25f, heldDetailFontSize = 23f, heldMaxTextWidth = 230f, heldDetailMaxWidth = 330f;
        public bool HeldSheet { get; private set; }
        private bool usingPaperLook;
        private Material toyMaterial;
        private static Material bubblePaper;
        private void SetPaperLook(bool on)
        {
            usingPaperLook = on;
            var renderer = GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                if (toyMaterial == null) toyMaterial = renderer.sharedMaterial;
                if (bubblePaper == null) bubblePaper = Resources.Load<Material>("BubblePaper");
                renderer.sharedMaterial = on && bubblePaper != null ? bubblePaper : toyMaterial;
            }
            paper.UsePaperLook(on);
            fittedKey = null;
        }
        public const float CardCornerRadius = .006f;
        public const float PaperTitleFont = 28f, PaperDetailFont = 20f; // BubbleLab's sizes
        // Stretched open in the hand: the summary as a heading and the whole sentence below it,
        // as on the BubbleLab paper.
        public bool HeldBoth { get; private set; }
        private Vector2 heldTitleSize, heldDetailSize;
        private float heldTitleFont, heldDetailFont;

        public static readonly Color ToyInk = new Color(.165f, .165f, .18f, 1f);
        public static readonly Color NoteInk = new Color(.27f, .23f, .18f, 1f);
        public static readonly Color ToyRingOutline = new Color(.62f, .62f, .67f, 1f);

        public bool IsUser { get; private set; }
        public Color BadgeColor => badge == null ? Color.clear : badge.color;
        public Vector3 PointerDirectionWorld { get; private set; }
        public float PointerTargetAlignment { get; private set; }
        public bool IsPopping => toyStyle && Time.time - popStartedAt < popDuration;
        public int PopCount { get; private set; }
        public bool IsThinking { get; private set; }
        // Pulled close to the face: larger type and the full sentence, like reading paper up close.
        public bool Reading { get; set; }
        public float ThinkingAmount => thinkingAmount;

        private readonly Vector3[] pointerVertices = new Vector3[3];
        private readonly Vector3[] pointerNormals = new Vector3[3];
        private TangibleBubble paper;
        private TransferableSpeechBubble transfer;
        private Mesh runtimePointerMesh;
        private bool topicNote;
        private bool guideCaption;
        private float popStartedAt = -10f;
        private string poppedText;
        private bool scaledByPop;
        private bool popFeedbackPending;
        private float thinkingAmount;
        private RectTransform thinkingDots;
        private ToyTextMirror titleMirror, detailMirror;
        private bool wasCrumpled, wasHeld;
        private string fittedKey;
        private Vector2 fittedText, fittedCard;
        private float fittedFont, fittedPadLeft, fittedPadRight;
        public Vector2 FittedCardSize => fittedCard;
        public float FittedFontSize => fittedFont;
        const float PadSide = 11f, PadDot = 29f, PadVertical = 9f;

        private void Awake()
        {
            paper = GetComponent<TangibleBubble>();
            transfer = GetComponent<TransferableSpeechBubble>();
            if (paper != null)
            {
                // Paper cards with crisp, nearly square corners (not pills).
                if (toyStyle) paper.toyCornerRadius = CardCornerRadius;
                if (toyStyle) paper.UseToyShape();
                else paper.UseMeetingPaperShape();
            }
            if (Application.isPlaying)
            {
                if (toyStyle) CreateToyTailMesh();
                else CreateRuntimePointerMesh();
                if (toyStyle) ApplyToyInk();
            }
            SetAuthor(authorName);
        }

        private void OnEnable()
        {
            if (paper == null) paper = GetComponent<TangibleBubble>();
            SetAuthor(authorName);
            if (toyStyle && Application.isPlaying) StartPop();
        }

        public void StartPop()
        {
            popStartedAt = Time.time;
            popFeedbackPending = true;
            PopCount++;
        }

        private void ApplyToyInk()
        {
            if (paper == null) return;
            foreach (var text in new[] { paper.title, paper.detail })
            {
                if (text == null) continue;
                text.color = ToyInk;
                // Typewriter for the legacy fallback path; the TMP mirror has its own.
                if (text.GetComponent<TypewriterReveal>() == null) text.gameObject.AddComponent<TypewriterReveal>();
            }
            titleMirror = Mirror(paper.title, true);
            detailMirror = Mirror(paper.detail, true);
            Mirror(badgeLabel, false);
        }

        private ToyTextMirror Mirror(Text text, bool reveal)
        {
            if (text == null || toyFont == null) return null;
            var mirror = text.GetComponent<ToyTextMirror>();
            if (mirror == null) mirror = text.gameObject.AddComponent<ToyTextMirror>();
            mirror.Initialize(toyFont, reveal);
            return mirror;
        }

        private void EnsureThinkingDots()
        {
            if (thinkingDots != null || paper == null || paper.ink == null) return;
            var root = new GameObject("Thinking Dots", typeof(RectTransform));
            thinkingDots = (RectTransform)root.transform;
            thinkingDots.SetParent(paper.ink.transform, false);
            thinkingDots.sizeDelta = new Vector2(200f, 60f);
            for (int i = 0; i < 3; i++)
            {
                var dot = new GameObject("Dot " + i, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                dot.rectTransform.SetParent(thinkingDots, false);
                dot.rectTransform.sizeDelta = new Vector2(8f, 8f);
                dot.rectTransform.anchoredPosition = new Vector2((i - 1) * 12f, 0f);
                dot.sprite = badge != null ? badge.sprite : null;
                dot.color = new Color(.6f, .6f, .66f, 1f);
                dot.raycastTarget = false;
            }
            root.SetActive(false);
        }

        private void UpdateThinkingDots(bool visible)
        {
            if (visible) EnsureThinkingDots();
            if (thinkingDots == null) return;
            if (thinkingDots.gameObject.activeSelf != visible) thinkingDots.gameObject.SetActive(visible);
            if (!visible) return;
            for (int i = 0; i < thinkingDots.childCount; i++)
            {
                var dot = (RectTransform)thinkingDots.GetChild(i);
                float bounce = Mathf.Max(0f, Mathf.Sin(Time.time * 7f - i * .9f));
                dot.anchoredPosition = new Vector2((i - 1) * 12f, bounce * bounce * 4f);
            }
        }

        private void ApplyToyBadge()
        {
            if (!toyStyle || badge == null) return;
            var outline = badge.GetComponent<Outline>();
            bool information = !IsUser && string.Equals(authorName, "Information", StringComparison.OrdinalIgnoreCase);
            if (outline == null) return;
            // Coloured roles read as a clean dot; the white Information dot gets a soft ring.
            outline.enabled = information;
            outline.effectColor = ToyRingOutline;
            outline.effectDistance = new Vector2(1.6f, -1.6f);
        }

        public void SetAuthor(string author)
        {
            authorName = string.IsNullOrWhiteSpace(author) ? "Information" : author.Trim();
            IsUser = IsUserAuthor(authorName);
            if (badge != null) badge.color = ResolveRoleColor(authorName);
            if (badgeLabel != null)
            {
                badgeLabel.text = IsUser ? "Me" : string.Empty;
                badgeLabel.color = new Color(.96f, .96f, .94f, 1f);
                badgeLabel.gameObject.SetActive(IsUser);
            }
            ApplyToyBadge();
        }

        public void SetTopicNote(bool enabled)
        {
            topicNote = enabled;
            if (pointerRenderer != null) pointerRenderer.enabled = !enabled;
        }

        public void UseGuideCaption(bool enabled = true)
        {
            guideCaption = enabled;
            if (paper == null) paper = GetComponent<TangibleBubble>();
            if (paper.title == null) return;
            paper.title.resizeTextForBestFit = true;
            paper.title.resizeTextMinSize = 24;
            paper.title.resizeTextMaxSize = 36;
            paper.title.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        public static bool IsUserAuthor(string author)
        {
            return string.Equals(author, "Me", StringComparison.OrdinalIgnoreCase)
                || string.Equals(author, "User", StringComparison.OrdinalIgnoreCase)
                || string.Equals(author, "You", StringComparison.OrdinalIgnoreCase);
        }

        public static Color ResolveRoleColor(string author)
        {
            if (IsUserAuthor(author)) return new Color(.09f, .105f, .13f, 1f);
            if (string.Equals(author, "Emotions", StringComparison.OrdinalIgnoreCase)) return new Color(.95f, .30f, .45f, 1f);
            if (string.Equals(author, "Benefit", StringComparison.OrdinalIgnoreCase)) return new Color(.95f, .61f, .10f, 1f);
            if (string.Equals(author, "Caution", StringComparison.OrdinalIgnoreCase)) return new Color(.24f, .27f, .35f, 1f);
            if (string.Equals(author, "Creativity", StringComparison.OrdinalIgnoreCase)) return new Color(.30f, .78f, .46f, 1f);
            if (string.Equals(author, "Decisions", StringComparison.OrdinalIgnoreCase)) return new Color(.25f, .55f, 1f, 1f);
            // Information is the white character. Keep the dot white as well; the UI
            // outline applied by the prefab keeps it visible against the paper card.
            return new Color(.98f, .98f, .96f, 1f);
        }

        private void CreateRuntimePointerMesh()
        {
            if (pointerMeshFilter == null || runtimePointerMesh != null) return;
            runtimePointerMesh = new Mesh { name = "SpeechBubbleUserPointer_Runtime" };
            runtimePointerMesh.MarkDynamic();
            runtimePointerMesh.vertices = pointerVertices;
            runtimePointerMesh.triangles = new[] { 0, 1, 2 };
            runtimePointerMesh.uv = new[] { Vector2.zero, Vector2.right, new Vector2(.5f, 1f) };
            pointerMeshFilter.sharedMesh = runtimePointerMesh;
        }

        // Rounded tail slab with the same thickness and edge tint as the card; local origin is
        // the card's lower edge so the presentation only moves and scales its transform.
        private void CreateToyTailMesh()
        {
            if (pointerMeshFilter == null || runtimePointerMesh != null) return;
            const float halfWidth = .013f, length = .019f, tipRadius = .0045f, overlap = .009f;
            float front = .0006f;
            float back = (paper != null ? paper.toyThickness : .007f) - .0008f;
            Color edge = paper != null ? paper.toyEdgeTint : new Color(.88f, .88f, .92f, 1f);
            Color rear = paper != null ? paper.toyBackTint : new Color(.95f, .95f, .97f, 1f);
            var outline = new System.Collections.Generic.List<Vector2> { new Vector2(-halfWidth, overlap) };
            var tipCenter = new Vector2(0f, -length + tipRadius);
            for (int i = 0; i <= 6; i++)
            {
                float angle = Mathf.Lerp(205f, 335f, i / 6f) * Mathf.Deg2Rad;
                outline.Add(tipCenter + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * tipRadius);
            }
            outline.Add(new Vector2(halfWidth, overlap));
            int n = outline.Count;
            Vector2 centroid = Vector2.zero;
            foreach (var point in outline) centroid += point / n;

            var positions = new System.Collections.Generic.List<Vector3>();
            var normals = new System.Collections.Generic.List<Vector3>();
            var tints = new System.Collections.Generic.List<Color>();
            var triangles = new System.Collections.Generic.List<int>();
            // Front fan (faces -Z, toward the viewer) and back fan.
            for (int face = 0; face < 2; face++)
            {
                float z = face == 0 ? front : back;
                int start = positions.Count;
                positions.Add(new Vector3(centroid.x, centroid.y, z));
                foreach (var point in outline) positions.Add(new Vector3(point.x, point.y, z));
                for (int i = 0; i <= n; i++) { normals.Add(face == 0 ? Vector3.back : Vector3.forward); tints.Add(face == 0 ? Color.white : rear); }
                for (int i = 0; i < n; i++)
                {
                    int a = start + 1 + i, b = start + 1 + (i + 1) % n;
                    if (face == 0) triangles.AddRange(new[] { start, b, a });
                    else triangles.AddRange(new[] { start, a, b });
                }
            }
            // Side walls, one flat-shaded quad per outline edge.
            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = outline[i], p1 = outline[(i + 1) % n];
                Vector2 along = (p1 - p0).normalized;
                var outward = new Vector3(along.y, -along.x, 0f);
                int start = positions.Count;
                positions.Add(new Vector3(p0.x, p0.y, front));
                positions.Add(new Vector3(p1.x, p1.y, front));
                positions.Add(new Vector3(p0.x, p0.y, back));
                positions.Add(new Vector3(p1.x, p1.y, back));
                for (int k = 0; k < 4; k++) { normals.Add(outward); tints.Add(edge); }
                triangles.AddRange(new[] { start, start + 1, start + 2, start + 1, start + 3, start + 2 });
            }
            runtimePointerMesh = new Mesh { name = "SpeechBubbleToyTail_Runtime" };
            runtimePointerMesh.SetVertices(positions);
            runtimePointerMesh.SetNormals(normals);
            runtimePointerMesh.SetColors(tints);
            runtimePointerMesh.SetTriangles(triangles, 0);
            runtimePointerMesh.RecalculateBounds();
            pointerMeshFilter.sharedMesh = runtimePointerMesh;
            pointerMeshFilter.transform.localRotation = Quaternion.identity;
        }

        private static float EaseOutBack(float x)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            x = Mathf.Clamp01(x) - 1f;
            return 1f + c3 * x * x * x + c1 * x * x;
        }

        // Sound and a small puff when a docked card pops in; paper crunch on crumple/restore.
        private void PlayToyFeedback(bool docked, float height)
        {
            if (popFeedbackPending && Time.time - popStartedAt > popDuration) popFeedbackPending = false;
            if (popFeedbackPending && docked && !IsThinking)
            {
                popFeedbackPending = false;
                Vector3 tip = transform.TransformPoint(new Vector3(0f, -height * .5f - .02f, 0f));
                ToySounds.Play(ToySounds.Cue.Pop, tip, .55f, RolePitch());
                ToyFx.Puff(tip, .01f, 2);
            }
            bool crumpled = paper != null && paper.IsCrumpled;
            if (crumpled != wasCrumpled)
            {
                ToySounds.Play(ToySounds.Cue.Paper, transform.position, .8f, crumpled ? .9f : 1.15f);
                if (crumpled) ToyFx.Puff(transform.position, .016f, 3);
                wasCrumpled = crumpled;
            }
        }

        private float RolePitch()
        {
            switch (authorName)
            {
                case "Emotions": return 1.12f;
                case "Benefit": return 1.06f;
                case "Caution": return .88f;
                case "Creativity": return 1.18f;
                case "Decisions": return .94f;
                default: return IsUser ? .82f : 1f;
            }
        }

        private void ResetPopScale()
        {
            if (!scaledByPop) return;
            transform.localScale = Vector3.one;
            scaledByPop = false;
        }

        // Pop the docked card from its tail tip, then let the tail follow.
        private void UpdateToyPresentation(float height)
        {
            string current = paper != null && paper.title != null ? paper.title.text : null;
            if (current != poppedText)
            {
                poppedText = current;
                if (!string.IsNullOrEmpty(current)) StartPop();
            }
            bool held = transfer != null && transfer.IsHeld;
            bool docked = transfer != null && transfer.Owner != null && transfer.Owner.isActiveAndEnabled && !held;
            // Released onto a (new) character: land with a pop.
            if (docked && wasHeld && transfer.Owner != null) StartPop();
            wasHeld = held;

            // "…" while this card's character is thinking: shrink to a small pill of dots.
            var character = docked ? transfer.Owner.character : null;
            bool thinking = character != null && character.authoredBubble == transfer && character.Cue == "thinking"
                && (paper == null || !paper.IsCrumpled);
            if (IsThinking && !thinking) StartPop();
            IsThinking = thinking;
            thinkingAmount = Mathf.MoveTowards(thinkingAmount, thinking ? 1f : 0f, Time.deltaTime * 5f);
            bool showDots = thinkingAmount > .5f;
            UpdateThinkingDots(showDots);
            if (titleMirror != null) titleMirror.Hidden = showDots;
            if (detailMirror != null) detailMirror.Hidden = showDots;

            float age = Time.time - popStartedAt;
            float popScale = age < popDuration ? Mathf.LerpUnclamped(.25f, 1f, EaseOutBack(age / popDuration)) : 1f;
            // Thinking shrinks the card itself to a pill (FitCard), not the transform.
            float scale = popScale;
            if (docked && !Mathf.Approximately(scale, 1f))
            {
                transform.localScale = Vector3.one * scale;
                // The dock rewrites localPosition every frame before this (execution order 100).
                transform.localPosition -= Vector3.up * (height * .5f + .024f) * (1f - scale);
                scaledByPop = true;
            }
            else ResetPopScale();

            float tail = docked ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(popDuration * .4f, popDuration, age)) : 0f;
            bool visible = docked && (paper == null || paper.Crumple < .025f) && tail > .01f;
            PlayToyFeedback(docked, height);
            if (pointerRenderer != null) pointerRenderer.enabled = visible;
            var tailTransform = pointerMeshFilter.transform;
            tailTransform.localPosition = new Vector3(0f, -height * .5f, 0f);
            tailTransform.localScale = Vector3.one * Mathf.Max(.001f, tail);
            PointerDirectionWorld = transform.TransformDirection(Vector3.down);
            PointerTargetAlignment = 0f;
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || pointerMeshFilter == null) return;
            if (topicNote)
            {
                ResetPopScale();
                if (pointerRenderer != null) pointerRenderer.enabled = false;
                if (paper != null)
                {
                    if (paper.title != null) paper.title.gameObject.SetActive(false);
                    if (paper.detail != null)
                    {
                        paper.detail.gameObject.SetActive(paper.IsOpen && paper.Crumple < .1f);
                        if (toyStyle) { LayoutNoteText(); if (badge != null) badge.gameObject.SetActive(false); return; }
                        var detailRect = paper.detail.rectTransform;
                        detailRect.anchorMin = new Vector2(.5f, .5f);
                        detailRect.anchorMax = new Vector2(.5f, .5f);
                        detailRect.anchoredPosition = Vector2.zero;
                        detailRect.sizeDelta = new Vector2((paper.Width - .075f) * 1000f, 205f);
                        paper.detail.alignment = TextAnchor.MiddleCenter;
                        paper.detail.horizontalOverflow = HorizontalWrapMode.Wrap;
                        paper.detail.verticalOverflow = VerticalWrapMode.Truncate;
                        paper.detail.resizeTextForBestFit = true;
                        int noteMax = toyStyle ? Mathf.RoundToInt(topicNoteFontSize) : 52;
                        paper.detail.fontSize = noteMax;
                        paper.detail.resizeTextMinSize = toyStyle ? 16 : 22;
                        paper.detail.resizeTextMaxSize = noteMax;
                    }
                    if (badge != null) badge.gameObject.SetActive(false);
                }
                return;
            }
            float stretch = paper == null ? 0f : Mathf.InverseLerp(paper.compactWidth, paper.openWidth, paper.Width);
            float width = paper == null ? .38f : paper.Width;
            float height = paper == null ? .22f : paper.Height;
            if (toyStyle)
            {
                if (runtimePointerMesh == null) CreateToyTailMesh();
                // In the hand the card becomes the BubbleLab paper (shape, material, layout).
                bool paperLook = !guideCaption && transfer != null && transfer.IsHeld && thinkingAmount <= .5f;
                if (paperLook != usingPaperLook) SetPaperLook(paperLook);
                UpdateToyPresentation(height);
                FitCard();
                LayoutToyInk();
                return;
            }
            UpdatePaperPointer(stretch, width, height);
            LayoutInk(width);
        }

        // Size the card to what it shows (or to a small pill while thinking). A presented
        // line shows its full body; otherwise the summary. Lines are balanced so no line
        // ends up with a single short word.
        private void FitCard()
        {
            if (paper == null || paper.title == null) return;
            bool thinking = thinkingAmount > .5f;
            bool showDetail = ShowsDetail();
            var shown = showDetail ? paper.detail : paper.title;
            var mirror = showDetail ? detailMirror : titleMirror;
            string text = shown.text ?? string.Empty;
            bool legacy = mirror == null || mirror.UsingLegacyFallback;
            bool held = !guideCaption && !thinking && !topicNote && transfer != null && transfer.IsHeld;
            if (!held) HeldBoth = false;
            string key = text + "|" + guideCaption + "|" + legacy + "|" + thinking + "|" + showDetail + "|" + Reading + "|" + held +
                         "|" + (held ? paper.title.text + "|" + paper.detail?.text : "");
            if (key == fittedKey) return;
            fittedKey = key;
            HeldSheet = held;
            if (held)
            {
                FitHeldSheet(showDetail);
                return;
            }
            if (thinking)
            {
                fittedCard = ThinkingPillSize;
            }
            else
            {
                float maxWidth = guideCaption ? guideMaxTextWidth : showDetail ? cardMaxTextWidth + 32f : cardMaxTextWidth;
                float size = guideCaption ? guideFontSize : cardFontSize;
                if (Reading) { maxWidth += 80f; size += 5f; }
                Vector2 measured = Measure(mirror, shown, text, size, maxWidth, legacy);
                while (measured.y > (Reading ? 230f : cardMaxTextHeight) && size > cardMinFontSize)
                {
                    size -= 1f;
                    measured = Measure(mirror, shown, text, size, maxWidth, legacy);
                }
                fittedFont = size;
                fittedText = new Vector2(Mathf.Max(measured.x, 24f), Mathf.Max(measured.y, size * 1.2f));
                fittedPadLeft = guideCaption ? PadSide + 4f : PadDot;
                fittedPadRight = guideCaption ? PadSide + 4f : PadSide + 2f;
                fittedCard = new Vector2(
                    Mathf.Max(guideCaption ? 90f : 62f, fittedText.x + fittedPadLeft + fittedPadRight) / 1000f,
                    Mathf.Max(guideCaption ? 54f : 36f, fittedText.y + PadVertical * 2f) / 1000f);
            }
            paper.compactWidth = fittedCard.x;
            paper.compactHeight = fittedCard.y;
            if (showDetail || thinking || Reading)
            {
                // A presented line is already at its reading size.
                paper.openWidth = fittedCard.x;
                paper.openHeight = fittedCard.y;
            }
            else
            {
                // The two-hand "open" stretch grows modestly instead of to a 52 cm sheet.
                paper.openWidth = Mathf.Min(Mathf.Max(fittedCard.x * 1.22f, fittedCard.x + .05f), .34f);
                paper.openHeight = Mathf.Min(fittedCard.y * 1.25f + .012f, .24f);
            }
        }

        // Both sizes are fitted up front (summary = compact, sentence = open) so the two-hand
        // stretch has a stable range and never flips back and forth between them.
        private void FitHeldSheet(bool showDetail)
        {
            string summary = paper.title.text ?? string.Empty;
            string sentence = paper.detail != null ? paper.detail.text ?? string.Empty : string.Empty;
            bool hasDetail = !string.IsNullOrWhiteSpace(sentence) && sentence != summary;
            FitSheet(paper.title, titleMirror, summary, PaperTitleFont, 240f, out var compactText, out float compactFont);
            Vector2 compact = new Vector2(Mathf.Max(280f, compactText.x + 60f), Mathf.Max(180f, compactText.y + 90f)) / 1000f;
            Vector2 open = new Vector2(Mathf.Max(520f, compact.x * 1000f * 1.6f), 260f) / 1000f, openText = compactText;
            float openFont = compactFont;
            if (hasDetail)
            {
                FitSheet(paper.detail, detailMirror, sentence, PaperDetailFont, 440f, out openText, out openFont);
                float contentWidth = Mathf.Max(compactText.x, openText.x);
                float contentHeight = compactText.y + 16f + openText.y;
                open = new Vector2(Mathf.Max(520f, contentWidth + 80f), Mathf.Max(260f, contentHeight + 70f)) / 1000f;
            }
            HeldBoth = showDetail && hasDetail;
            heldTitleSize = compactText; heldTitleFont = compactFont;
            heldDetailSize = openText; heldDetailFont = openFont;
            fittedText = compactText;
            fittedFont = compactFont;
            fittedPadLeft = PadDot + 6f;
            fittedPadRight = PadSide + 8f;
            fittedCard = HeldBoth ? open : compact;
            paper.compactWidth = compact.x;
            paper.compactHeight = compact.y;
            paper.openWidth = open.x;
            paper.openHeight = open.y;
        }

        private Vector2 FitSheet(Text shown, ToyTextMirror mirror, string text, float size, float maxWidth, out Vector2 textSize, out float font)
        {
            bool legacy = mirror == null || mirror.UsingLegacyFallback;
            Vector2 measured = Measure(mirror, shown, text, size, maxWidth, legacy);
            while (measured.y > 210f && size > 16f)
            {
                size -= 1f;
                measured = Measure(mirror, shown, text, size, maxWidth, legacy);
            }
            font = size;
            textSize = new Vector2(Mathf.Max(measured.x, 40f), Mathf.Max(measured.y, size * 1.2f));
            // At least BubbleLab's compact sheet (28 x 9 cm) so a second hand can take the other side.
            return new Vector2(Mathf.Max(260f, textSize.x + PadDot + 6f + PadSide + 8f) / 1000f,
                               Mathf.Max(90f, textSize.y + PadVertical * 2f + 14f) / 1000f);
        }

        private bool ShowsDetail()
        {
            return !guideCaption && thinkingAmount <= .5f && (paper.IsOpen || Reading) && paper.detail != null && paper.title != null &&
                   !string.IsNullOrWhiteSpace(paper.detail.text) && paper.detail.text != paper.title.text;
        }

        // Preferred text box (canvas units). TMP path: the widest line at maxWidth, then the
        // narrowest width that keeps the same number of lines ("balanced" wrapping).
        private static Vector2 Measure(ToyTextMirror mirror, Text shown, string text, float size, float maxWidth, bool legacy)
        {
            if (string.IsNullOrEmpty(text)) return new Vector2(24f, size * 1.2f);
            if (!legacy && mirror != null && mirror.Target != null)
            {
                var tmp = mirror.Target;
                string display = mirror.DisplayText(text);
                tmp.enableAutoSizing = false;
                tmp.fontSize = size;
                float width = Mathf.Min(maxWidth, tmp.GetPreferredValues(display, maxWidth, 0f).x);
                float height = tmp.GetPreferredValues(display, width + 1f, 0f).y;
                float low = width * .4f, high = width;
                for (int i = 0; i < 9; i++)
                {
                    float middle = (low + high) * .5f;
                    if (tmp.GetPreferredValues(display, middle + 1f, 0f).y <= height + .5f) high = middle;
                    else low = middle;
                }
                return new Vector2(high + 3f, height);
            }
            var generator = shown.cachedTextGeneratorForLayout;
            var settings = shown.GetGenerationSettings(new Vector2(maxWidth, 10000f));
            settings.fontSize = Mathf.RoundToInt(size);
            settings.resizeTextForBestFit = false;
            settings.horizontalOverflow = HorizontalWrapMode.Wrap;
            settings.verticalOverflow = VerticalWrapMode.Overflow;
            float unit = Mathf.Max(.0001f, shown.pixelsPerUnit);
            float wrappedHeight = generator.GetPreferredHeight(text, settings) / unit;
            settings.horizontalOverflow = HorizontalWrapMode.Overflow;
            float freeWidth = generator.GetPreferredWidth(text, settings) / unit;
            return new Vector2(Mathf.Min(maxWidth, freeWidth) + 2f, wrappedHeight);
        }

        private void LayoutToyInk()
        {
            if (paper == null) return;
            float width = paper.Width * 1000f, height = paper.Height * 1000f;
            if (paper.ink != null) ((RectTransform)paper.ink.transform).sizeDelta = new Vector2(width, height);
            if (paper.textPanel != null) paper.textPanel.sizeDelta = new Vector2(width, height);
            bool dots = thinkingDots != null && thinkingDots.gameObject.activeSelf;
            if (HeldSheet && !dots) { LayoutHeldBoth(width, height); return; }
            bool showDetail = ShowsDetail();
            var shown = showDetail ? paper.detail : paper.title;
            var hidden = showDetail ? paper.title : paper.detail;
            if (hidden != null && hidden.gameObject.activeSelf) hidden.gameObject.SetActive(false);
            if (shown != null)
            {
                if (!shown.gameObject.activeSelf) shown.gameObject.SetActive(true);
                var rect = shown.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
                rect.sizeDelta = fittedText + new Vector2(2f, 2f);
                rect.anchoredPosition = new Vector2((fittedPadLeft - fittedPadRight) * .5f, 0f);
                shown.alignment = guideCaption ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft;
                shown.resizeTextForBestFit = false;
                shown.horizontalOverflow = HorizontalWrapMode.Wrap;
                shown.verticalOverflow = VerticalWrapMode.Overflow;
                int size = Mathf.RoundToInt(fittedFont > 0f ? fittedFont : cardFontSize);
                if (shown.fontSize != size) shown.fontSize = size;
            }
            if (badge != null)
            {
                badge.gameObject.SetActive(!guideCaption && !dots);
                badge.rectTransform.sizeDelta = new Vector2(12f, 12f);
                float lineHeight = Mathf.Max(1f, (fittedFont > 0f ? fittedFont : cardFontSize) * 1.22f);
                float firstLine = fittedText.y * .5f - Mathf.Min(fittedText.y, lineHeight) * .5f;
                badge.rectTransform.anchoredPosition = new Vector2(-width * .5f + 10f + 6f, firstLine);
            }
        }

        // BubbleLab layout: the title centred on the sheet; once stretched open, the title moves
        // up and the full sentence appears centred below it.
        private void LayoutHeldBoth(float width, float height)
        {
            bool both = HeldBoth && paper.detail != null && paper.IsOpen && paper.Crumple < .1f;
            float total = both ? heldTitleSize.y + 16f + heldDetailSize.y : heldTitleSize.y;
            void Place(Text text, Vector2 size, float y, float font)
            {
                if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
                var rect = text.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
                rect.sizeDelta = size + new Vector2(2f, 2f);
                rect.anchoredPosition = new Vector2(0f, y);
                text.alignment = TextAnchor.MiddleCenter;
                text.resizeTextForBestFit = false;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                int px = Mathf.RoundToInt(font);
                if (text.fontSize != px) text.fontSize = px;
            }
            float titleY = total * .5f - heldTitleSize.y * .5f;
            Place(paper.title, heldTitleSize, titleY, heldTitleFont);
            if (both) Place(paper.detail, heldDetailSize, -total * .5f + heldDetailSize.y * .5f, heldDetailFont);
            else if (paper.detail != null && paper.detail.gameObject.activeSelf) paper.detail.gameObject.SetActive(false);
            if (badge != null)
            {
                // The role colour as a small dot in the top-left corner of the sheet.
                badge.gameObject.SetActive(true);
                badge.rectTransform.sizeDelta = new Vector2(14f, 14f);
                badge.rectTransform.anchoredPosition = new Vector2(-width * .5f + 22f, height * .5f - 22f);
            }
        }

        // Sticky note: the whole topic, centred and balanced, sized to the note (large while
        // held, small once it sits in the ring).
        private string noteKey;
        private Vector2 noteText;
        private float noteFont;
        private void LayoutNoteText()
        {
            var detail = paper.detail;
            float w = paper.Width * 1000f, h = paper.Height * 1000f;
            float pad = Mathf.Max(7f, w * .1f), strip = h * .16f;
            float boxWidth = Mathf.Max(20f, w - pad * 2f), boxHeight = Mathf.Max(12f, h - pad * 2f - strip * .6f);
            string text = detail.text ?? string.Empty;
            bool legacy = detailMirror == null || detailMirror.UsingLegacyFallback;
            string key = text + "|" + Mathf.Round(w / 4f) + "|" + Mathf.Round(h / 4f) + "|" + legacy;
            if (key != noteKey)
            {
                noteKey = key;
                float size = Mathf.Clamp(h * .105f, 9f, topicNoteFontSize);
                Vector2 measured = Measure(detailMirror, detail, text, size, boxWidth, legacy);
                while (measured.y > boxHeight && size > 8f)
                {
                    size -= 1f;
                    measured = Measure(detailMirror, detail, text, size, boxWidth, legacy);
                }
                noteFont = size;
                noteText = measured;
            }
            var rect = detail.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = new Vector2(0f, -strip * .3f);
            rect.sizeDelta = noteText + new Vector2(2f, 2f);
            detail.alignment = TextAnchor.MiddleCenter;
            if (detail.fontStyle != FontStyle.Normal) detail.fontStyle = FontStyle.Normal;
            if (detail.color != NoteInk) detail.color = NoteInk;
            detail.resizeTextForBestFit = false;
            detail.horizontalOverflow = HorizontalWrapMode.Wrap;
            detail.verticalOverflow = VerticalWrapMode.Overflow;
            int fontSize = Mathf.RoundToInt(noteFont);
            if (detail.fontSize != fontSize) detail.fontSize = fontSize;
        }

        private void UpdatePaperPointer(float stretch, float width, float height)
        {
            if (runtimePointerMesh == null) CreateRuntimePointerMesh();
            if (runtimePointerMesh == null) return;
            // The tail belongs to the lower edge, in the same plane as the paper.
            // Hide it before the sheet folds so no rigid spike survives on the ball.
            if (pointerRenderer != null) pointerRenderer.enabled = paper == null || paper.Crumple < .025f;
            Vector3 baseCenter = new Vector3(-width * .12f, -height * .5f + .004f, .003f);
            float halfWidth = Mathf.Lerp(.016f, .020f, stretch);
            Vector3 first = baseCenter - Vector3.right * halfWidth;
            Vector3 second = baseCenter + Vector3.right * halfWidth;
            Vector3 tip = baseCenter + new Vector3(.010f, -.028f, 0f);
            // Match the paper's visible (-Z) face so the same material does not shade black.
            if (Vector3.Dot(Vector3.Cross(second - first, tip - first), Vector3.back) < 0f)
            {
                Vector3 swap = first;
                first = second;
                second = swap;
            }
            pointerVertices[0] = first;
            pointerVertices[1] = second;
            pointerVertices[2] = tip;
            pointerNormals[0] = pointerNormals[1] = pointerNormals[2] = Vector3.back;
            runtimePointerMesh.vertices = pointerVertices;
            runtimePointerMesh.normals = pointerNormals;
            runtimePointerMesh.RecalculateBounds();

            PointerDirectionWorld = transform.TransformDirection(tip - baseCenter).normalized;
            PointerTargetAlignment = 0f; // No longer points toward the viewer in 3D.
        }

        private void LayoutInk(float width)
        {
            // Compact cards use the whole face for the main sentence. Expanded cards move
            // the summary upward to make room for the detail text.
            if (paper != null && paper.title != null)
            {
                var titleRect = paper.title.rectTransform;
                titleRect.anchoredPosition = new Vector2(guideCaption ? 0f : 18f, paper.IsOpen && !guideCaption ? 28f : 0f);
                titleRect.sizeDelta = new Vector2(guideCaption ? -16f : -58f, paper.IsOpen && !guideCaption ? 60f : 108f);
                if (guideCaption && paper.detail != null) paper.detail.gameObject.SetActive(false);
            }
            if (badge != null)
            {
                badge.gameObject.SetActive(!guideCaption && !(thinkingDots != null && thinkingDots.gameObject.activeSelf));
                float panelWidth = Mathf.Max(1f, (width - .04f) * 1000f);
                badge.rectTransform.anchoredPosition = new Vector2(-panelWidth * .5f + 19f, paper != null && paper.IsOpen ? 28f : 0f);
            }
        }

        private void OnDestroy()
        {
            if (runtimePointerMesh != null) Destroy(runtimePointerMesh);
        }
    }
}
