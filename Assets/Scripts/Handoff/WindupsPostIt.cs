using UnityEngine;
using Windups.BubbleLab;

namespace Windups.Handoff
{
    // The topic sticky note drawn with the designed post-its (Resources/WindupsPostIt_A/B/C and
    // WindupsPostIt_Deck, from Assets/Art/WindupsCharacter/Props). The note keeps its card behaviour
    // (TangibleBubble size, text, grab); only its look changes while it is in the sticky-note shape:
    //  - the guide offers the pad (deck) with the note as its top sheet;
    //  - grabbing the note pulls off one of A, B or C (a new one each time) and the pad pops away.
    public sealed class WindupsPostIt : MonoBehaviour
    {
        public const float ModelSize = .036f, DeckSize = .0384f;
        public static readonly string[] Variants = { "A", "B", "C" };

        public string Variant { get; private set; } = "A";
        public bool DeckShown => deck != null && deck.gameObject.activeSelf;
        public Transform Deck => deck;

        private TangibleBubble paper;
        private MeshRenderer card;
        private MeshFilter filter;
        private Transform look, deck;
        private readonly GameObject[] sheets = new GameObject[3];
        private bool following;
        private float deckGoneAt = -1f, deckShownAt;

        private void Awake()
        {
            paper = GetComponent<TangibleBubble>();
            card = GetComponent<MeshRenderer>();
            filter = GetComponent<MeshFilter>();
            look = new GameObject("Post-it Look").transform;
            look.SetParent(transform, false);
            look.localRotation = Quaternion.Euler(-90f, 0f, 0f); // the sheet's face (+Y) toward the reader (-Z)
            for (int i = 0; i < 3; i++)
            {
                var model = Resources.Load<GameObject>("WindupsPostIt_" + Variants[i]);
                if (model == null) continue;
                sheets[i] = Instantiate(model, look, false);
                sheets[i].name = "Post-it " + Variants[i];
                sheets[i].SetActive(false);
            }
            Roll();
        }

        public bool Ready => sheets[0] != null;

        // A new sheet: one of A, B or C.
        public void Roll()
        {
            int pick = Random.Range(0, 3);
            Variant = Variants[pick];
            for (int i = 0; i < 3; i++) if (sheets[i] != null) sheets[i].SetActive(i == pick);
        }

        // The guide holds out the pad: the note is its top sheet.
        public void OfferFromDeck()
        {
            if (!Ready) return;
            if (deck == null)
            {
                var model = Resources.Load<GameObject>("WindupsPostIt_Deck");
                if (model == null) return;
                deck = Instantiate(model).transform;
                deck.name = "Post-it Pad";
            }
            Roll();
            deck.gameObject.SetActive(true);
            following = true;
            keptScale = -1f;
            deckGoneAt = -1f;
            deckShownAt = Time.time;
            FollowNote(1f);
        }

        // Grabbed off the pad: a fresh sheet comes off, the pad stays a moment and pops away
        // (`keepPad`: the idea wall's pad stays where it is for the next sheet).
        public void PullFromDeck(bool keepPad = false)
        {
            Roll();
            following = false;
            if (deck == null || !deck.gameObject.activeSelf) return;
            // The idea wall's pad stays as big as it was (the peeled note opens; the pile must not grow with it).
            if (keepPad) { keptScale = DeckScale(); ToySounds.Play(ToySounds.Cue.Paper, deck.position, .7f, 1.3f); return; }
            deckGoneAt = Time.time + .7f;
            ToySounds.Play(ToySounds.Cue.Paper, deck.position, .7f, 1.3f);
        }

        private void LateUpdate()
        {
            bool sticky = paper != null && paper.IsStickyNote && Ready;
            if (card != null) card.forceRenderingOff = sticky;
            look.gameObject.SetActive(sticky && card != null && card.enabled);
            if (sticky && filter != null && filter.sharedMesh != null)
            {
                var b = filter.sharedMesh.bounds;
                float sx = Mathf.Max(.01f, b.size.x) / ModelSize, sz = Mathf.Max(.01f, b.size.y) / ModelSize;
                // The curl stays paper-thin on a big note.
                look.localScale = new Vector3(sx, Mathf.Min(2f, Mathf.Min(sx, sz)), sz);
                look.localPosition = new Vector3(b.center.x, b.center.y, .002f);
            }
            if (deck == null || !deck.gameObject.activeSelf) return;
            if (following) FollowNote(1f - Mathf.Exp(-Time.deltaTime * 20f));
            float grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.time - deckShownAt) / .25f));
            if (deckGoneAt > 0f)
            {
                float shrink = Mathf.Clamp01((Time.time - deckGoneAt) / .2f);
                grow *= 1f - shrink;
                if (shrink >= 1f)
                {
                    ToyFx.Puff(deck.position, .012f, 4);
                    deck.gameObject.SetActive(false);
                    deckGoneAt = -1f;
                    return;
                }
            }
            deck.localScale = Vector3.one * (keptScale > 0f && !following ? keptScale : DeckScale()) * grow;
        }

        private float keptScale = -1f;

        private float DeckScale()
        {
            float w = paper != null ? paper.Width : .072f;
            return Mathf.Max(.5f, w * 1.06f / DeckSize);
        }

        // Behind the note, the same way up: its top face just under the note.
        private void FollowNote(float blend)
        {
            float s = DeckScale();
            var rotation = transform.rotation * Quaternion.Euler(-90f, 0f, 0f);
            var position = transform.position + transform.forward * (.0144f * s + .003f) - transform.up * .002f;
            deck.SetPositionAndRotation(Vector3.Lerp(deck.position, position, blend), Quaternion.Slerp(deck.rotation, rotation, blend));
        }

        private void OnDestroy()
        {
            if (deck != null) Destroy(deck.gameObject);
        }
    }
}
