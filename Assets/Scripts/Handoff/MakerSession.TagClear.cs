using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    // 2026-11-13 (the user, after the Quest try: "sometimes the buttons I must press and the 'Maker' name tag overlap and it
    // looks broken"): the crew's name tags float 21 cm above their feet, right where a speech card and its reply pills
    // stand, and teammates stand only ~22 cm apart. Now a tag gives way: while, seen from the person's eyes, it overlaps
    // a speech card, the open question's pills, the look pictures, a shown tool or the colour wheel, it shrinks away, and
    // it comes back when the view is clear. It only changes the tag's scale (MakerSession's LateUpdate places it).
    // Four times a second, reusing its buffers (no lists or strings per tick).
    // 2026-11-13 (the user: "the name tags keep blinking - why?"): the tag's own rectangle was measured at its current
    // scale, so a shrinking tag stopped overlapping, grew back, overlapped again and shrank - over and over. Now the
    // tag is judged at its full size, it comes back only after the view has stayed clear for TagReturnAfter, and it
    // does not shrink again within TagHoldMin of coming back (no tag changes twice within a second).
    public sealed partial class MakerSession
    {
        public static float TagClearEvery = .25f, TagMargin = .012f, TagReturnAfter = 1.2f, TagHoldMin = 1.5f; // seconds; viewport fraction
        public int TagsHidden { get; private set; }               // tags hidden on the last tick (checks)
        public int TagHides { get; private set; }                 // times a tag gave way (checks)

        public int TagSwitches { get; private set; }               // hide or show changes (checks: no blinking)

        private float tagClearAt;
        private readonly List<Rect> tagBlockers = new List<Rect>(16);
        private readonly Dictionary<Crew, bool> tagHidden = new Dictionary<Crew, bool>();
        private readonly Dictionary<Crew, float> tagClearSince = new Dictionary<Crew, float>(), tagShownAt = new Dictionary<Crew, float>();
        private readonly Dictionary<Crew, Vector2> tagFullSize = new Dictionary<Crew, Vector2>();
        private readonly List<Renderer> tagRenderers = new List<Renderer>(32);
        private static readonly Vector3[] Corners = new Vector3[8];

        private void TagClearFrame()
        {
            if (!Begun || tags.Count == 0) return;
            var view = Camera.main;
            if (view == null) return;
            if (Time.time >= tagClearAt)
            {
                tagClearAt = Time.time + TagClearEvery;
                CollectTagBlockers(view);
                int hidden = 0;
                foreach (var pair in tags)
                {
                    if (pair.Value == null) continue;
                    bool blocked = false;
                    if (ViewRect(view, pair.Value, false, out var rect))
                    {
                        // At full size: the size last measured at full scale, about the centre it has now.
                        if (pair.Value.localScale.x >= .999f) tagFullSize[pair.Key] = rect.size;
                        else if (tagFullSize.TryGetValue(pair.Key, out var full)) rect = new Rect(rect.center - full * .5f, full);
                        foreach (var blocker in tagBlockers)
                            if (rect.Overlaps(blocker)) { blocked = true; break; }
                    }
                    tagHidden.TryGetValue(pair.Key, out bool was);
                    if (blocked) tagClearSince.Remove(pair.Key);
                    else if (!tagClearSince.ContainsKey(pair.Key)) tagClearSince[pair.Key] = Time.time;
                    bool hide = was
                        ? blocked || Time.time - tagClearSince[pair.Key] < TagReturnAfter                                  // back only after a clear while
                        : blocked && (!tagShownAt.TryGetValue(pair.Key, out float shownAt) || Time.time - shownAt >= TagHoldMin); // not again right away
                    // F66: a name tag shows only for the teammate the person looks at or comes close to.
                    if (HandRules && !TagWanted(pair.Key)) hide = true;
                    if (hide && !was) TagHides++;
                    if (hide != was) { TagSwitches++; if (!hide) tagShownAt[pair.Key] = Time.time; }
                    tagHidden[pair.Key] = hide;
                    if (hide) hidden++;
                }
                TagsHidden = hidden;
            }
            float step = Time.deltaTime * 8f;
            foreach (var pair in tags)
            {
                if (pair.Value == null) continue;
                tagHidden.TryGetValue(pair.Key, out bool hide);
                float s = Mathf.MoveTowards(pair.Value.localScale.x, hide ? 0f : 1f, step);
                pair.Value.localScale = Vector3.one * s;
            }
        }

        private void CollectTagBlockers(Camera view)
        {
            tagBlockers.Clear();
            foreach (var pair in cast)
            {
                var bubble = pair.Value != null ? pair.Value.authoredBubble : null;
                if (bubble != null && bubble.gameObject.activeInHierarchy && ViewRect(view, bubble.transform, true, out var rect)) tagBlockers.Add(rect);
            }
            if (Question != null && !Question.answered && Question.root != null && ViewRect(view, Question.root, true, out var pills)) tagBlockers.Add(pills);
            foreach (var look in Looks)
                if (look != null && look.root != null && look.root.gameObject.activeInHierarchy && ViewRect(view, look.root, true, out var picture)) tagBlockers.Add(picture);
            if (toolBook && paper != null && paper.gameObject.activeInHierarchy && ViewRect(view, paper, true, out var book)) tagBlockers.Add(book);
            if (gear != null && gear.gameObject.activeInHierarchy && ViewRect(view, gear, true, out var g)) tagBlockers.Add(g);
            if (magnifier != null && magnifier.gameObject.activeInHierarchy && ViewRect(view, magnifier, true, out var m)) tagBlockers.Add(m);
            if (wheelRoot != null && wheelRoot.gameObject.activeInHierarchy && ViewRect(view, wheelRoot, true, out var w)) tagBlockers.Add(w);
        }

        // The viewport rectangle a thing covers (its renderers' bounds), grown by TagMargin; false when behind the eyes.
        private bool ViewRect(Camera view, Transform root, bool children, out Rect rect)
        {
            rect = default;
            tagRenderers.Clear();
            if (children) root.GetComponentsInChildren(false, tagRenderers);
            else root.GetComponentsInChildren(false, tagRenderers);
            if (tagRenderers.Count == 0) return false;
            Bounds b = default;
            bool any = false;
            foreach (var r in tagRenderers)
            {
                if (r == null || !r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any) return false;
            Vector3 c = b.center, e = b.extents;
            for (int i = 0; i < 8; i++)
                Corners[i] = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 v = view.WorldToViewportPoint(Corners[i]);
                if (v.z <= 0f) return false;
                x0 = Mathf.Min(x0, v.x); y0 = Mathf.Min(y0, v.y); x1 = Mathf.Max(x1, v.x); y1 = Mathf.Max(y1, v.y);
            }
            rect = Rect.MinMaxRect(x0 - TagMargin, y0 - TagMargin, x1 + TagMargin, y1 + TagMargin);
            return true;
        }

        // Tests: whether a crew's name tag is giving way now.
        public bool TagGivingWay(Crew crew) => tagHidden.TryGetValue(crew, out bool hide) && hide;
        public Transform CrewNameTag(Crew crew) => tags.TryGetValue(crew, out var t) ? t : null;
    }
}
