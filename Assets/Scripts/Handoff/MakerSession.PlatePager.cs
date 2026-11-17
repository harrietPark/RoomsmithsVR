using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F94 (2026-11-15, the user after the Quest try: "v1, v2 are hard to see; why did v1 disappear? I'd like < > on the
    // plate: < shows v1, > shows v2 again. And when v2 first shows up as the see-through one, it should stand on the plate
    // too, with <"). The project's stand carries `<  v2  >` on its front rim, facing the person:
    //  - The stand always shows one version. < / > swap the one on it (the other goes small into its arrow, the new one
    //    pops up on the stand) and the one on the stand is the one worked on (Product, as F77's "the version on the
    //    stand"). Earlier versions are kept hidden (F81's notebook flight is replaced by a slip into <).
    //  - Change shape: the see-through next version stands ON the stand where the made one stood (not beside it), and
    //    the made one slips into <. < shows it again (the see-through one, its flags and size lines hide), > brings the
    //    see-through one back. Planting a flag, saying a change or Done shows the see-through one.
    //  - The label is big (the version tags above the things and over the see-through one are hidden while it shows);
    //    the see-through one's label is blueprint blue.
    //  - Only while the shape is reviewed (before Start colouring), with two things or more to show, and not while
    //    Maker carves. Pressed like every button: a resting fingertip (FingerPress), a pinch near or aimed from afar
    //    (FarButtonOnRay), an Editor click.
    // The made one stays active while the see-through one is up (too much reads it every frame); it is only not drawn
    // and not touchable (HideOnPlate: forceRenderingOff, colliders off, left out of MadeThings).
    // PlatePager = false keeps F40/F77/F81 (the see-through one beside, the old one flies into Maker's notebook).
    public sealed partial class MakerSession
    {
        public static bool PlatePager = true;
        public const float StandArrowSize = .026f, PagerLabelW = .05f, PagerLabelH = .03f, PagerGap = .006f, PagerOut = .016f, PagerLift = .004f;
        private static readonly Color PagerPaper = new Color(1f, .97f, .86f), PagerInk = new Color(.2f, .2f, .26f);
        private bool PlatePagerOn => PlatePager && VersionsIntoNotebook && FormFirst;

        private Transform platePager, pagerBackButton, pagerNextButton, pagerLabel;
        private TextMeshProUGUI pagerLabelText;
        private MeshRenderer pagerLabelCard;
        private string pagerLabelShown;
        private bool pagerLabelBlue;
        private bool pagerShowsDraft, draftOnStand; // (draftOnStand: MakeDraft put the see-through one on the stand)
        private float pagerBusyUntil, pagerPressedAt = -10f, pagerHideCheckAt;
        private Vector3 pagerFacing = Vector3.back;
        private readonly FingerPress pagerBackFinger = new FingerPress(), pagerNextFinger = new FingerPress();
        private readonly Dictionary<Transform, List<(Renderer renderer, bool wasOff)>> plateHiddenDraw = new Dictionary<Transform, List<(Renderer, bool)>>();
        private readonly Dictionary<Transform, List<(Collider collider, bool was)>> plateHiddenTouch = new Dictionary<Transform, List<(Collider, bool)>>();
        private readonly List<Renderer> pagerRenderers = new List<Renderer>();
        private readonly List<Collider> pagerColliders = new List<Collider>();

        public bool PlatePagerShown => platePager != null && platePager.gameObject.activeSelf;
        public string PlatePagerLabel => PlatePagerShown ? pagerLabelShown ?? "" : "";
        public bool PlatePagerBackShown => PlatePagerShown && pagerBackButton != null && pagerBackButton.gameObject.activeSelf;
        public bool PlatePagerNextShown => PlatePagerShown && pagerNextButton != null && pagerNextButton.gameObject.activeSelf;
        public Transform PlatePagerBackButton => pagerBackButton;
        public Transform PlatePagerNextButton => pagerNextButton;
        public bool DraftOnPlate => PlatePagerOn && Draft != null && draftOnStand && pagerShowsDraft;
        public bool DraftOnStand => Draft != null && draftOnStand;
        public bool HiddenOnPlate(Transform t) => t != null && plateHiddenDraw.ContainsKey(t);
        public int PlatePagerPresses { get; private set; }

        // ------------------------------------------------------------ what can be shown

        private readonly List<Version> pagerVersions = new List<Version>();
        private static readonly System.Comparison<Version> ByNumber = (a, b) => a.number.CompareTo(b.number);
        private readonly List<Transform> pagerHiddenKeys = new List<Transform>();
        private void PagerItems()
        {
            pagerVersions.Clear();
            foreach (var v in Versions) if (v != null && v.root != null) pagerVersions.Add(v);
            pagerVersions.Sort(ByNumber);
        }

        private int PagerCount => pagerVersions.Count + (Draft != null && draftOnStand ? 1 : 0);

        private int PagerIndex
        {
            get
            {
                if (Draft != null && draftOnStand && pagerShowsDraft) return pagerVersions.Count;
                for (int i = 0; i < pagerVersions.Count; i++) if (pagerVersions[i].root == Product) return i;
                return pagerVersions.Count - 1;
            }
        }

        private bool PagerWanted =>
            PlatePagerOn && projectPlate != null && projectPlate.gameObject.activeInHierarchy && Product != null &&
            Current == Step.Done && Review == ReviewStage.Shape && Sculpt == SculptStage.None && held != projectPlate;

        // ------------------------------------------------------------ hiding the made one on the stand

        private void HideOnPlate(Transform thing)
        {
            if (thing == null) return;
            if (!plateHiddenDraw.TryGetValue(thing, out var draws)) { draws = new List<(Renderer, bool)>(); plateHiddenDraw[thing] = draws; }
            if (!plateHiddenTouch.TryGetValue(thing, out var touches)) { touches = new List<(Collider, bool)>(); plateHiddenTouch[thing] = touches; }
            // (again every half second: a part split or a rebuild may give it new renderers)
            thing.GetComponentsInChildren(true, pagerRenderers);
            foreach (var r in pagerRenderers)
            {
                if (r == null || r.forceRenderingOff && draws.Exists(d => d.renderer == r)) continue;
                if (!draws.Exists(d => d.renderer == r)) draws.Add((r, r.forceRenderingOff));
                r.forceRenderingOff = true;
            }
            thing.GetComponentsInChildren(true, pagerColliders);
            foreach (var c in pagerColliders)
            {
                if (c == null) continue;
                if (!touches.Exists(d => d.collider == c)) touches.Add((c, c.enabled));
                c.enabled = false;
            }
        }

        private void ShowOnPlate(Transform thing)
        {
            if (thing == null) return;
            if (plateHiddenDraw.TryGetValue(thing, out var draws))
                foreach (var (r, wasOff) in draws) if (r != null) r.forceRenderingOff = wasOff;
            if (plateHiddenTouch.TryGetValue(thing, out var touches))
                foreach (var (c, was) in touches) if (c != null) c.enabled = was;
            plateHiddenDraw.Remove(thing);
            plateHiddenTouch.Remove(thing);
        }

        // ------------------------------------------------------------ every frame (frame step "plate pager")

        private void PlatePagerFrame()
        {
            if (!PlatePagerOn && plateHiddenDraw.Count == 0 && platePager == null) return;
            if (Draft == null) pagerShowsDraft = draftOnStand = false;
            KeepPlateShown();
            PagerItems();
            bool show = PagerWanted && PagerCount >= 2;
            if (!show)
            {
                if (platePager != null && platePager.gameObject.activeSelf) platePager.gameObject.SetActive(false);
                ShowThingTags(true);
                return;
            }
            BuildPlatePager();
            if (!platePager.gameObject.activeSelf) platePager.gameObject.SetActive(true);
            ShowThingTags(false);
            PlacePlatePager();
            int index = PagerIndex, count = PagerCount;
            bool draftShown = DraftOnPlate;
            int number = draftShown ? NextVersionNumber : index >= 0 && index < pagerVersions.Count ? pagerVersions[index].number : 1;
            SetPagerLabel("v" + number, draftShown);
            bool back = index > 0, next = index < count - 1;
            if (pagerBackButton.gameObject.activeSelf != back) pagerBackButton.gameObject.SetActive(back);
            if (pagerNextButton.gameObject.activeSelf != next) pagerNextButton.gameObject.SetActive(next);
            if (Time.time < pagerBusyUntil) return;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip))
                {
                    pagerBackFinger.Forget(hand);
                    pagerNextFinger.Forget(hand);
                    continue;
                }
                if (back && pagerBackFinger.Step(this, hand, OnStandArrow(pagerBackButton, tip.position, PressMargin, PressNear, PressThrough)))
                { PressLog("stand <", hand); PressPlatePager(-1); return; }
                if (next && pagerNextFinger.Step(this, hand, OnStandArrow(pagerNextButton, tip.position, PressMargin, PressNear, PressThrough)))
                { PressLog("stand >", hand); PressPlatePager(1); return; }
            }
        }

        // What should be drawn: the see-through one or the made one, never both on the stand.
        private void KeepPlateShown()
        {
            if (Time.time < pagerBusyUntil) return;
            bool draftUp = DraftOnPlate;
            bool inReview = Current == Step.Done && Review == ReviewStage.Shape;
            if (draftUp && Product != null && Product != Draft)
            {
                if (!plateHiddenDraw.ContainsKey(Product) || Time.time >= pagerHideCheckAt) { HideOnPlate(Product); pagerHideCheckAt = Time.time + .5f; }
                if (inReview && !Draft.gameObject.activeSelf) Draft.gameObject.SetActive(true);
            }
            else if (PlatePagerOn && Draft != null && draftOnStand && inReview && Draft.gameObject.activeSelf) Draft.gameObject.SetActive(false);
            // Anything hidden that should not be any more (the see-through one went, the stand shows another).
            if (plateHiddenDraw.Count > 0)
            {
                pagerHiddenKeys.Clear();
                pagerHiddenKeys.AddRange(plateHiddenDraw.Keys); // (a reused list: this runs every frame)
                foreach (var t in pagerHiddenKeys)
                    if (t == null) { plateHiddenDraw.Remove(t); plateHiddenTouch.Remove(t); }
                    else if (!(draftUp && t == Product)) ShowOnPlate(t);
            }
        }

        // The version tags above the things and the see-through one's tag: the stand's label says it while it shows.
        private void ShowThingTags(bool shown)
        {
            if (draftTag != null && draftTag.gameObject.activeSelf != shown) draftTag.gameObject.SetActive(shown);
            foreach (var v in Versions)
                if (v?.tag != null && v.tag.gameObject.activeSelf != shown) v.tag.gameObject.SetActive(shown);
        }

        // ------------------------------------------------------------ the buttons

        private void BuildPlatePager()
        {
            if (platePager != null) return;
            platePager = new GameObject("Stand Pager").transform;
            platePager.SetParent(transform, false);
            Color role = MakerRoles.Color(Crew.Maker), face = Color.Lerp(role, Color.white, .1f);
            pagerBackButton = PagerArrow("Stand Back", "<", face, role);
            pagerNextButton = PagerArrow("Stand Next", ">", face, role);
            pagerLabel = PaperTag("Stand Label", PagerLabelW, PagerLabelH, PagerPaper, out pagerLabelText, 20f, false);
            pagerLabel.SetParent(platePager, false);
            pagerLabel.localPosition = Vector3.zero;
            pagerLabelText.fontStyle = FontStyles.Bold;
            pagerLabelText.color = PagerInk;
            pagerLabelCard = pagerLabel.Find("Card")?.GetComponent<MeshRenderer>();
            float across = PagerLabelW * .5f + PagerGap + StandArrowSize * .5f;
            pagerBackButton.localPosition = new Vector3(-across, 0f, 0f);
            pagerNextButton.localPosition = new Vector3(across, 0f, 0f);
            platePager.gameObject.SetActive(false);
        }

        private Transform PagerArrow(string name, string glyph, Color face, Color role)
        {
            var pill = Panel(name, StandArrowSize, StandArrowSize, face, out var text, 18f);
            text.text = glyph;
            text.color = Color.white;
            text.fontStyle = FontStyles.Bold;
            MakeRaised(pill, StandArrowSize, StandArrowSize, face, Color.Lerp(role, Color.black, .2f));
            pill.SetParent(platePager, false);
            pill.localRotation = Quaternion.identity;
            return pill;
        }

        private void SetPagerLabel(string label, bool draft)
        {
            if (label == pagerLabelShown && draft == pagerLabelBlue) return;
            pagerLabelShown = label;
            pagerLabelBlue = draft;
            pagerLabelText.text = label;
            pagerLabelText.color = draft ? Color.white : PagerInk;
            if (pagerLabelCard != null)
            {
                var block = new MaterialPropertyBlock();
                pagerLabelCard.GetPropertyBlock(block);
                block.SetColor("_BaseColor", draft ? Blueprint : PagerPaper);
                block.SetFloat("_VertexColor", 0f);
                pagerLabelCard.SetPropertyBlock(block);
            }
        }

        // On the stand's front rim, toward the person, facing their eyes; the side moves only when they walk round.
        private void PlacePlatePager()
        {
            Vector3 top = StandTopOf(projectPlate);
            if (Head != null)
            {
                Vector3 toUser = Vector3.ProjectOnPlane(Head.position - top, Vector3.up);
                if (toUser.sqrMagnitude > 1e-4f && Vector3.Angle(toUser, pagerFacing) > 20f) pagerFacing = toUser.normalized;
            }
            Vector3 at = PagerCentre(pagerFacing);
            platePager.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at));
        }

        // F103 (Quest 2026-11-16, the user: "the < > can't be pressed: the plane hides them - put them higher"): on the
        // rim (1.9 cm over the desk, the arrows' lower edge 6 mm over it) a fingertip met the real desk before the button,
        // and the room's desk plane is not exactly the real top. The row now stands PagerRaise over the surface the stand
        // is on and PagerOutRaised in front of the rim, still below the model as seen from the eyes.
        public static bool PagerRaised = true;
        public const float PagerRaise = .042f, PagerOutRaised = .045f;
        private Vector3 PagerCentre(Vector3 facing)
        {
            Vector3 top = StandTopOf(projectPlate);
            float radius = StandRadiusOf(projectPlate);
            if (!PagerRaised) return top + facing * (radius + PagerOut) + Vector3.up * PagerLift;
            float floor = projectPlate.position.y;
            return new Vector3(top.x, Mathf.Max(top.y + PagerLift, floor + PagerRaise), top.z) + facing * (radius + PagerOutRaised);
        }

        // Checks: how high the arrows' lower edge is over the surface the stand is on.
        public float PagerArrowClearance => platePager != null && projectPlate != null && pagerBackButton != null
            ? Mathf.Min(pagerBackButton.position.y, pagerNextButton.position.y) - StandArrowSize * .5f - projectPlate.position.y : 0f;

        private bool OnStandArrow(Transform button, Vector3 world, float margin, float back, float front)
        {
            if (button == null || !button.gameObject.activeInHierarchy || float.IsInfinity(world.x)) return false;
            Vector3 l = button.InverseTransformPoint(world);
            return Mathf.Abs(l.x) <= StandArrowSize * .5f + margin && Mathf.Abs(l.y) <= StandArrowSize * .5f + margin && l.z >= back && l.z <= front;
        }

        private int PlatePagerOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            if (!PlatePagerShown || Time.time < pagerBusyUntil) return 0;
            if (ArrowOnRay(pagerBackButton, ray, out distance)) return -1;
            if (ArrowOnRay(pagerNextButton, ray, out distance)) return 1;
            return 0;
        }

        private bool ArrowOnRay(Transform button, Ray ray, out float distance)
        {
            distance = 0f;
            if (button == null || !button.gameObject.activeInHierarchy) return false;
            if (!new Plane(button.forward, button.position).Raycast(ray, out float d) || d <= 0f || d > 4f) return false;
            if (!OnStandArrow(button, ray.GetPoint(d), .004f + d * .01f, -.01f, .01f)) return false;
            distance = d;
            return true;
        }

        // A pinch near an arrow, aimed at it from afar, or an Editor click.
        private bool PlatePagerPinch(Vector3 point, Ray? ray)
        {
            if (!PlatePagerShown || Time.time < pagerBusyUntil) return false;
            int step = 0;
            if (PlatePagerBackShown && OnStandArrow(pagerBackButton, point, .012f, -.03f, .03f)) step = -1;
            else if (PlatePagerNextShown && OnStandArrow(pagerNextButton, point, .012f, -.03f, .03f)) step = 1;
            else if (ray.HasValue) step = PlatePagerOnRay(ray.Value, out _);
            if (step == 0) return false;
            PressPlatePager(step);
            return true;
        }

        // Checks and the Editor press it here too.
        public void PressPlatePager(int step)
        {
            if (!PlatePagerShown || Time.time < pagerBusyUntil || Time.time - pagerPressedAt < PagerPressGap) return;
            if (held != null && (held == Product || held == Draft)) return; // (not while the one on the stand is in a hand)
            var button = step < 0 ? pagerBackButton : pagerNextButton;
            if (button == null || !button.gameObject.activeSelf) return;
            pagerPressedAt = Time.time;
            PlatePagerPresses++;
            PressRaised(button);
            ToySounds.Play(ToySounds.Cue.Press, button.position, .5f, step < 0 ? 1.4f : 1.6f);
            PagerItems();
            int index = PagerIndex + (step < 0 ? -1 : 1);
            if (index < 0 || index >= PagerCount) return;
            if (index == pagerVersions.Count) PagerShowDraft(true);
            else PagerShowVersion(pagerVersions[index], step);
        }

        // ------------------------------------------------------------ swapping what stands on the stand

        // The see-through next version on the stand (the made one goes into <).
        private void PagerShowDraft(bool animate)
        {
            if (!PlatePagerOn || Draft == null || !draftOnStand || pagerShowsDraft) return;
            pagerShowsDraft = true;
            WriteInputLog("stand shows v" + NextVersionNumber + " (see-through)", true);
            if (!animate || Product == null || HiddenOnPlate(Product)) return;
            pagerBusyUntil = Time.time + .3f;
            StartCoroutine(SlipToArrow(Product, -1, false));
            Draft.gameObject.SetActive(true);
            var scale = Draft.localScale;
            Draft.localScale = scale * .2f;
            StartCoroutine(PopTo(Draft, scale, .3f));
        }

        // A made version on the stand: the one there goes small into the arrow on its side, this one pops up.
        private void PagerShowVersion(Version version, int step)
        {
            if (version?.root == null) return;
            var incoming = version.root;
            bool fromDraft = DraftOnPlate;
            pagerShowsDraft = false;
            pagerBusyUntil = Time.time + .3f;
            if (fromDraft && Draft.gameObject.activeSelf) StartCoroutine(SlipToArrow(Draft, step < 0 ? 1 : -1, true));
            if (incoming == Product)
            {
                // (the made one under the see-through one; the stand may have been carried meanwhile)
                ShowOnPlate(Product);
                Product.gameObject.SetActive(true);
                if (!StandsOn(Product, projectPlate)) PutOnStand(Product);
                var full = Product.localScale;
                Product.localScale = full * .2f;
                StartCoroutine(PopTo(Product, full, .3f));
            }
            else
            {
                var outgoing = Product;
                if (outgoing != null)
                {
                    ShowOnPlate(outgoing);
                    if (outgoing.gameObject.activeSelf && !fromDraft) StartCoroutine(SlipToArrow(outgoing, step < 0 ? 1 : -1, true));
                    else outgoing.gameObject.SetActive(false);
                    ForgetRoomItem(outgoing);
                }
                incoming.gameObject.SetActive(true);
                ShowOnPlate(incoming);
                PutOnStand(incoming);
                var full = incoming.localScale;
                incoming.localScale = full * .2f;
                StartCoroutine(PopTo(incoming, full, .3f));
                Product = incoming;
                ProductMesh = incoming.GetComponentInChildren<MeshFilter>()?.sharedMesh;
                ProductBody = incoming.GetComponent<Rigidbody>();
                Design = version.design.Copy();
                productFinished = true;
                endingLastProduct = Product;
                endingLastDesign = Design.Copy();
                if (shapeRecords.TryGetValue(incoming, out var record))
                { LookJpeg = FormJpeg = record.image; ShapeTaskId = record.task; Look = record.look; }
                ShowShapeReference();
                Remember();
            }
            WriteInputLog("stand shows v" + version.number, true);
            ToyFx.Puff(StandTopOf(projectPlate) + Vector3.up * .01f, .008f, 3);
        }

        private void PutOnStand(Transform thing)
        {
            if (projectPlate == null || thing == null) return;
            var mesh = thing.GetComponentInChildren<Renderer>();
            if (mesh == null) return;
            var bounds = mesh.bounds;
            var top = StandTopOf(projectPlate);
            thing.position += top - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            SyncBody(thing);
        }

        // Where an arrow is, or would be (before the pager first shows).
        private Vector3 ArrowPoint(int side)
        {
            var button = side < 0 ? pagerBackButton : pagerNextButton;
            if (PlatePagerShown && button != null) return button.position;
            if (projectPlate == null) return transform.position;
            Vector3 top = StandTopOf(projectPlate);
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - top, Vector3.up).normalized : Vector3.back;
            Vector3 right = Vector3.Cross(Vector3.up, toUser);
            return PagerCentre(toUser) + right * (side < 0 ? 1f : -1f) * (PagerLabelW * .5f + PagerGap + StandArrowSize * .5f);
        }

        // Small into the arrow; then hidden (`deactivate`: put away; else only not drawn: the made one under the
        // see-through one) and back to its own place and size.
        private IEnumerator SlipToArrow(Transform thing, int side, bool deactivate, Vector3? restAt = null, Quaternion? restTurn = null)
        {
            if (thing == null) yield break;
            Vector3 from = thing.position, scale = thing.localScale;
            Vector3 back = restAt ?? from; // F102: where it waits (on the stand), when it came from elsewhere
            Vector3 to = ArrowPoint(side);
            ToySounds.Play(ToySounds.Cue.Whoosh, from, .3f, 1.6f);
            for (float t = 0f; t < 1f && thing != null; t += Time.deltaTime / .25f)
            {
                float e = Mathf.SmoothStep(0f, 1f, t);
                thing.position = Vector3.Lerp(from, to, e) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .03f;
                thing.localScale = scale * Mathf.Lerp(1f, .1f, e);
                yield return null;
            }
            if (thing == null) yield break;
            thing.position = back;
            if (restTurn.HasValue) thing.rotation = restTurn.Value;
            thing.localScale = scale;
            SyncBody(thing);
            if (deactivate) { ShowOnPlate(thing); thing.gameObject.SetActive(false); }
            else HideOnPlate(thing);
        }

        private static IEnumerator PopTo(Transform thing, Vector3 scale, float seconds)
        {
            Vector3 start = thing != null ? thing.localScale : scale;
            for (float t = 0f; t < 1f && thing != null; t += Time.deltaTime / seconds)
            {
                float e = 1f - Mathf.Pow(1f - t, 3f);
                thing.localScale = Vector3.LerpUnclamped(start, scale, e + Mathf.Sin(t * Mathf.PI) * .08f);
                yield return null;
            }
            if (thing != null) thing.localScale = scale;
        }

        // F81's notebook flight, with the pager: the version leaves the stand into < (already there when the
        // see-through one stood on the stand).
        private IEnumerator IntoPager(Transform thing)
        {
            VersionsNotebooked++;
            if (thing == null) yield break;
            if (HiddenOnPlate(thing))
            {
                ShowOnPlate(thing);
                thing.gameObject.SetActive(false);
            }
            else yield return SlipToArrow(thing, -1, true);
            WriteInputLog("version into the stand's <: " + (thing != null ? thing.name : "?"), true);
        }
    }
}
