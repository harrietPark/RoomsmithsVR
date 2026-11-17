using System;
using System.Collections;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // Onboarding "dealt trading card" and afterwards the teammate's name tag.
    // Information holds the six cards face-down; each flies to its owner, flips to a
    // profile card (name in a role-coloured header, role, three things it can help with),
    // holds for reading, then shrinks into a small name tag floating above the head.
    // Tapping the tag opens the profile again; tapping again closes it. World-locked:
    // everything turns toward where the user is (SpeechBubbleDock.FacingAt) and never follows the user's gaze.
    public sealed class CharacterIntroCard : MonoBehaviour
    {
        const float CardWidth = .22f, BaseHeight = .128f, LineStep = .019f, CardRadius = .018f, Thickness = .007f;
        // (2026-11-13) Three job lines fit the base card; each line more makes it LineStep taller (the intro cards list every tool).
        private float CardHeight = BaseHeight;
        const float TagHeight = .03f;
        private float TagWidth = .1f;
        const float DeckScale = .45f;

        public enum State { Hidden, Deck, Flying, Revealed, Collapsing, Tag, Expanded }
        public State Current { get; private set; } = State.Hidden;
        public string Role { get; private set; }
        public string RoleLine { get; private set; }
        public string[] Jobs { get; private set; }
        public bool Showing => Current == State.Flying || Current == State.Revealed || Current == State.Expanded;
        public bool IsTagReady => Current == State.Tag || Current == State.Expanded;
        public Vector3 TagPoint => tagPoint;
        // F81: the top of the name tag while the card is (or is becoming) a tag over the head; speech cards stay above it.
        public bool ShowsTag => Current == State.Tag || Current == State.Collapsing;
        public float TagTop => tagPoint.y + TagHeight * .5f;
        public Vector2 CardSize => new Vector2(CardWidth, CardHeight);
        // The character this card belongs to (hover/tap target besides the tag itself).
        public Transform Owner { get; set; }
        // Name tags stay hidden until a hand, a pointing ray or the Editor mouse hovers the
        // character (or its tag); they linger briefly after the hover ends.
        public bool Hovered { get; private set; }
        public bool TagVisible => tagScale > .5f;
        private float hoverUntil;
        public void SetHovered(bool hovered)
        {
            Hovered = hovered;
            if (hovered) hoverUntil = Time.time + .45f;
        }

        private static readonly Color Ink = new Color(.165f, .165f, .18f, 1f);
        private static readonly Color Muted = new Color(.40f, .40f, .45f, 1f);
        private static readonly Color Edge = new Color(.88f, .88f, .92f, 1f);
        private static readonly Color Rear = new Color(.95f, .95f, .97f, 1f);
        private static readonly Color CardBack = new Color(.86f, .9f, .97f, 1f);
        private readonly System.Collections.Generic.List<Mesh> meshes = new System.Collections.Generic.List<Mesh>();
        private Transform card, cardFront, cardBack, tag;
        private float pitch = 1f;
        private Vector3 cardPosition, tagPoint;
        private float cardScale = DeckScale, flip = 1f, tagScale, tilt;
        private bool showFront, bob;
        private float expandAt;

        public static CharacterIntroCard Create(Transform parent, string role, string roleLine, string[] jobs, Color roleColor,
            Material material, TMP_FontAsset font, float soundPitch)
        {
            var go = new GameObject("Intro Card " + role);
            go.transform.SetParent(parent, false);
            go.layer = parent != null ? parent.gameObject.layer : 0;
            var intro = go.AddComponent<CharacterIntroCard>();
            intro.pitch = soundPitch;
            intro.Build(role, roleLine, jobs ?? new string[0], roleColor, material, font);
            go.SetActive(false);
            return intro;
        }

        private void Build(string role, string roleLine, string[] jobs, Color roleColor, Material material, TMP_FontAsset font)
        {
            Role = role;
            RoleLine = roleLine;
            Jobs = jobs;
            CardHeight = BaseHeight + Mathf.Max(0, jobs.Length - 3) * LineStep;
            // The tag grows with the name so "Information" fits.
            TagWidth = Mathf.Max(.085f, .038f + role.Length * .0078f);
            bool light = roleColor.grayscale > .72f;
            Color onBand = light ? Ink : Color.white;

            // Profile card (front) and its face-down back.
            card = new GameObject("Profile").transform;
            card.SetParent(transform, false);
            cardFront = new GameObject("Front").transform;
            cardFront.SetParent(card, false);
            cardBack = new GameObject("Back").transform;
            cardBack.SetParent(card, false);
            AddMesh(cardFront, ToyMeshes.RoundedSlab(CardWidth, CardHeight, CardRadius, Thickness, Color.white, Edge, Rear, "ProfileCard"), Vector3.zero, material);
            const float band = .036f, inset = .006f;
            float bandY = CardHeight * .5f - inset - band * .5f;
            AddMesh(cardFront, ToyMeshes.RoundedSlab(CardWidth - inset * 2f, band, .012f, .002f, roleColor, light ? Edge : roleColor * .85f, roleColor, "ProfileBand"),
                new Vector3(0f, bandY, -.0012f), material);
            AddMesh(cardBack, ToyMeshes.RoundedSlab(CardWidth, CardHeight, CardRadius, Thickness, CardBack, Edge, Rear, "ProfileBack"), Vector3.zero, material);
            for (int i = 0; i < 3; i++)
                AddMesh(cardBack, ToyMeshes.Disc(.011f, .003f, Color.white, Edge, "BackDot"), new Vector3((i - 1) * .034f, 0f, -.0016f), material);

            var canvas = Canvas(cardFront, CardWidth, CardHeight, -.0024f);
            float left = -CardWidth * 500f + 16f, textWidth = CardWidth * 1000f - 32f;
            Text(canvas, role, font, 24f, FontStyles.Bold, onBand, TextAlignmentOptions.Left,
                new Vector2(left + textWidth * .5f, bandY * 1000f), new Vector2(textWidth, band * 1000f));
            Text(canvas, roleLine, font, 17f, FontStyles.Bold, Muted, TextAlignmentOptions.Left,
                new Vector2(left + textWidth * .5f, bandY * 1000f - band * 500f - 13f), new Vector2(textWidth, 22f));
            float lineY = bandY * 1000f - band * 500f - 35f;
            foreach (string job in jobs)
            {
                Text(canvas, "•  " + job, font, 16f, FontStyles.Normal, Ink, TextAlignmentOptions.Left,
                    new Vector2(left + textWidth * .5f, lineY), new Vector2(textWidth, 20f));
                lineY -= 19f;
            }

            // Name tag: a small pill with the role dot and the name.
            tag = new GameObject("Tag").transform;
            tag.SetParent(transform, false);
            AddMesh(tag, ToyMeshes.RoundedSlab(TagWidth, TagHeight, TagHeight * .5f, .006f, Color.white, Edge, Rear, "NameTag"), Vector3.zero, material);
            AddMesh(tag, ToyMeshes.Disc(.0075f, .003f, roleColor, light ? new Color(.7f, .7f, .75f) : roleColor * .85f, "NameTagDot"),
                new Vector3(-TagWidth * .5f + .015f, 0f, -.0016f), material);
            var tagCanvas = Canvas(tag, TagWidth, TagHeight, -.0012f);
            Text(tagCanvas, role, font, 17f, FontStyles.Bold, Ink, TextAlignmentOptions.Left,
                new Vector2(9f, 0f), new Vector2(TagWidth * 1000f - 34f, TagHeight * 1000f));
            tag.gameObject.SetActive(false);
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
        }

        private RectTransform Canvas(Transform parent, float width, float height, float z)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Canvas));
            go.layer = gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = new Vector2(width * 1000f, height * 1000f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, 0f, z);
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            return rect;
        }

        private static void Text(RectTransform parent, string value, TMP_FontAsset font, float size, FontStyles style,
            Color color, TextAlignmentOptions alignment, Vector2 at, Vector2 box)
        {
            var go = new GameObject("Line", typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = box;
            rect.anchoredPosition = at;
            var text = go.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
        }

        // Face-down in Information's hands, stacked by index.
        public void PlaceInDeck(Vector3 point, int index)
        {
            gameObject.SetActive(true);
            Current = State.Deck;
            cardPosition = point + Vector3.up * (index * .005f);
            cardScale = DeckScale; flip = 1f; tilt = 10f; tagScale = 0f;
            showFront = false; bob = false;
            Apply();
        }

        // Fly to the owner, flip to the profile, hold for reading, then shrink into the tag.
        public IEnumerator Deal(Vector3 reveal, Vector3 tagAt, float hold, Action onReveal)
        {
            gameObject.SetActive(true);
            SetTagPoint(tagAt);
            Vector3 start = cardPosition;
            Current = State.Flying;
            ToySounds.Play(ToySounds.Cue.Paper, start, .7f, 1.3f);
            float startScale = cardScale;
            for (float t = 0f; t < 1f; t += Time.deltaTime / 1.05f)
            {
                float e = Mathf.SmoothStep(0f, 1f, t);
                cardPosition = Vector3.Lerp(start, reveal, e) + Vector3.up * (Mathf.Sin(e * Mathf.PI) * .12f);
                cardScale = Mathf.Lerp(startScale, 1f, e);
                tilt = Mathf.Lerp(10f, 0f, e);
                yield return null;
            }
            cardPosition = reveal; cardScale = 1f; tilt = 0f;
            for (float t = 0f; t < 1f; t += Time.deltaTime / .45f)
            {
                showFront = t >= .5f;
                flip = Mathf.Abs(Mathf.Cos(t * Mathf.PI));
                if (showFront && Current == State.Flying)
                {
                    Current = State.Revealed;
                    ToySounds.Play(ToySounds.Cue.Pop, reveal, .75f, pitch);
                    ToyFx.Puff(reveal, .012f, 4);
                    onReveal?.Invoke();
                }
                yield return null;
            }
            flip = 1f; showFront = true; bob = true;
            Current = State.Revealed;
            yield return new WaitForSeconds(hold);
            yield return CollapseToTag();
        }

        // Pops straight up out of the owner's head (no dealing), holds for reading, then
        // shrinks back down into the name tag.
        public IEnumerator PopUp(Vector3 reveal, Vector3 tagAt, float hold, Action onReveal)
        {
            float until = -1f;
            yield return PopUpWhile(reveal, tagAt, () => until < 0f || Time.time < until, () => { until = Time.time + .5f + hold; onReveal?.Invoke(); });
        }

        // 2026-11-13 (the maker introduction waits for the person's >): pops up and stays while `keep` is true.
        public IEnumerator PopUpWhile(Vector3 reveal, Vector3 tagAt, Func<bool> keep, Action onReveal)
        {
            gameObject.SetActive(true);
            SetTagPoint(tagAt);
            Current = State.Revealed;
            showFront = true; flip = 1f; tilt = 0f; tagScale = 0f; bob = false;
            cardPosition = tagAt; cardScale = .05f;
            Apply();
            ToySounds.Play(ToySounds.Cue.Pop, tagAt, .75f, pitch);
            ToyFx.Puff(tagAt, .01f, 4);
            onReveal?.Invoke();
            for (float t = 0f; t < 1f; t += Time.deltaTime / .5f)
            {
                float rise = Mathf.SmoothStep(0f, 1f, t);
                // Ease-out-back: a little overshoot, like a pop-up card.
                float k = t - 1f, grow = 1f + 2.2f * k * k * k + 1.2f * k * k;
                cardPosition = Vector3.Lerp(tagAt, reveal, rise);
                cardScale = Mathf.LerpUnclamped(.05f, 1f, grow);
                yield return null;
            }
            cardPosition = reveal; cardScale = 1f; bob = true;
            while (keep()) yield return null;
            yield return CollapseToTag();
        }

        // While revealed: glide to a new place (the introduction keeps it off Maker's speech card).
        public void MoveRevealed(Vector3 to)
        {
            if (Current != State.Revealed) return;
            cardPosition = Vector3.Lerp(cardPosition, to, 1f - Mathf.Exp(-Time.deltaTime * 10f));
        }
        public Vector3 RevealedAt => cardPosition;

        private IEnumerator CollapseToTag()
        {
            Current = State.Collapsing;
            bob = false;
            Vector3 from = cardPosition;
            for (float t = 0f; t < 1f; t += Time.deltaTime / .5f)
            {
                float e = Mathf.SmoothStep(0f, 1f, t);
                cardPosition = Vector3.Lerp(from, tagPoint, e);
                cardScale = Mathf.Lerp(1f, .3f, e);
                tagScale = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.55f, 1f, t));
                yield return null;
            }
            cardScale = 0f; tagScale = 1f;
            ToySounds.Play(ToySounds.Cue.Press, tagPoint, .45f, 1.2f);
            // Show where the tag lives for a moment, then it hides until hovered.
            hoverUntil = Time.time + 1.4f;
            Current = State.Tag;
        }

        // Tap on the tag (or the open card): open the full profile above the tag, or close it.
        public void Toggle()
        {
            if (Current == State.Tag)
            {
                WindupBody.Near(Owner)?.Play("Touch_Jump");
                Current = State.Expanded;
                expandAt = Time.time;
                showFront = true; flip = 1f; tilt = 0f; bob = false;
                cardPosition = ExpandedPoint;
                ToySounds.Play(ToySounds.Cue.Pop, cardPosition, .6f, pitch);
            }
            else if (Current == State.Expanded)
            {
                Current = State.Tag;
                expandAt = Time.time;
                ToySounds.Play(ToySounds.Cue.Press, tagPoint, .4f, 1.1f);
            }
        }

        public void Collapse() { if (Current == State.Expanded) Toggle(); }

        private Vector3 ExpandedPoint => tagPoint + Vector3.up * (CardHeight * .5f + TagHeight * .5f + .012f);

        // Hit tests in the card's own plane (for poke, pinch-ray and Editor mouse). While only
        // the tag is available, pointing at the character itself counts too.
        public bool Raycast(Ray ray, out float distance)
        {
            distance = 0f;
            if (Current == State.Expanded) return PlaneHit(card, ray, out distance);
            if (Current != State.Tag) return false;
            if (PlaneHit(tag, ray, out distance)) return true;
            if (Owner == null) return false;
            // A sphere around the capsule character.
            Vector3 toCenter = Owner.position - ray.origin;
            float along = Vector3.Dot(toCenter, ray.direction);
            if (along <= 0f) return false;
            if ((toCenter - ray.direction * along).sqrMagnitude > .075f * .075f) return false;
            distance = along;
            return true;
        }

        private bool PlaneHit(Transform target, Ray ray, out float distance)
        {
            var plane = new Plane(target.forward, target.position);
            if (!plane.Raycast(ray, out distance)) return false;
            return InBounds(target, ray.GetPoint(distance), .004f);
        }

        // A fingertip close to the character or where its tag floats.
        public bool Near(Vector3 point)
        {
            if (!IsTagReady) return false;
            if (Vector3.Distance(point, tagPoint) < .07f) return true;
            return Owner != null && Vector3.Distance(point, Owner.position) < .12f;
        }

        public bool Touches(Vector3 point)
        {
            Transform target = Current == State.Expanded ? card : Current == State.Tag ? tag : null;
            if (target == null) return false;
            Vector3 local = target.InverseTransformPoint(point);
            return Mathf.Abs(local.z) < .025f && InBounds(target, point, .006f);
        }

        private bool InBounds(Transform target, Vector3 point, float margin)
        {
            Vector3 local = target.InverseTransformPoint(point);
            float w = target == tag ? TagWidth : CardWidth, h = target == tag ? TagHeight : CardHeight;
            return Mathf.Abs(local.x) <= w * .5f + margin && Mathf.Abs(local.y) <= h * .5f + margin;
        }

        // F97 (Quest 2026-11-15, the user: "when the characters move their name tags should move too; now they don't,
        // after the introduction"): the tag was a fixed point where the card popped up. It now keeps its offset from
        // its character, so the tag (and the profile opened from it) goes wherever the character walks.
        private Vector3 tagOffset;
        private bool followsOwner;
        private void SetTagPoint(Vector3 tagAt)
        {
            tagPoint = tagAt;
            followsOwner = Owner != null;
            if (followsOwner) tagOffset = tagAt - Owner.position;
        }

        private void FollowOwner()
        {
            if (!followsOwner || Owner == null) return;
            if (Current != State.Collapsing && Current != State.Tag && Current != State.Expanded) return;
            Vector3 was = tagPoint;
            tagPoint = Owner.position + tagOffset;
            if (Current == State.Expanded) cardPosition += tagPoint - was;
        }

        private void LateUpdate()
        {
            if (Current == State.Hidden) return;
            FollowOwner();
            if (Current == State.Expanded)
            {
                cardScale = Mathf.LerpUnclamped(.3f, 1f, Overshoot(Mathf.Clamp01((Time.time - expandAt) / .35f)));
                tagScale = Mathf.MoveTowards(tagScale, 1f, Time.deltaTime * 8f);
            }
            else if (Current == State.Tag)
            {
                if (cardScale > 0f) cardScale = Mathf.MoveTowards(cardScale, 0f, Time.deltaTime * 5f);
                tagScale = Mathf.MoveTowards(tagScale, Time.time < hoverUntil ? 1f : 0f, Time.deltaTime * 7f);
            }
            Apply();
        }

        private static float Overshoot(float x)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            x -= 1f;
            return 1f + c3 * x * x * x + c1 * x * x;
        }

        private void Apply()
        {
            // Each card and tag turns to face where the user is (not where they look).
            var facing = SpeechBubbleDock.FacingAt(cardPosition);
            float wave = bob ? Mathf.Sin(Time.time * 2.4f) * .004f : 0f;
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            transform.localScale = Vector3.one;
            bool cardVisible = cardScale > .01f;
            if (card.gameObject.activeSelf != cardVisible) card.gameObject.SetActive(cardVisible);
            if (cardVisible)
            {
                card.SetPositionAndRotation(cardPosition + Vector3.up * wave, facing * Quaternion.Euler(tilt, 0f, 0f));
                card.localScale = new Vector3(Mathf.Max(.02f, flip) * cardScale, cardScale, cardScale);
                if (cardFront.gameObject.activeSelf != showFront) cardFront.gameObject.SetActive(showFront);
                if (cardBack.gameObject.activeSelf == showFront) cardBack.gameObject.SetActive(!showFront);
            }
            bool tagVisible = tagScale > .01f;
            if (tag.gameObject.activeSelf != tagVisible) tag.gameObject.SetActive(tagVisible);
            if (tagVisible)
            {
                tag.SetPositionAndRotation(tagPoint, SpeechBubbleDock.FacingAt(tagPoint));
                tag.localScale = Vector3.one * tagScale;
            }
        }

        private void OnDestroy()
        {
            foreach (var mesh in meshes) if (mesh != null) Destroy(mesh);
        }
    }
}
