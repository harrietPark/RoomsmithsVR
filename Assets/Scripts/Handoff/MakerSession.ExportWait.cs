using System.Collections;
using UnityEngine;

namespace Windups.Handoff
{
    // F87 (Quest 2026-11-15: "I don't like Fixer's animation while Export waits" - it ran Work_Loop under an endless
    // "..." bubble): while the files are packed and sent, Fixer stands by the work and thinks (Think_Loop, looking at it),
    // its bubble says what is happening ("Packing your files..." then "Sending 2 of 4..."), and one small paper plane
    // flies up off the work for every file that reached the web. Done: a nod, no Work_Done hammer.
    public sealed partial class MakerSession
    {
        public string ExportLeftOut { get; private set; } = "";
        public int ExportPlanes { get; private set; }
        public string ExportCaption { get; private set; } = "";
        private Transform exportThing;
        private Mesh planeMesh;

        private void BeginExportWait(Transform thing)
        {
            exportThing = thing;
            var body = Body(Crew.Fixer);
            body?.StartWork("Think_Loop");
            if (thing != null) LookAt(Crew.Fixer, thing.position, 60f);
            ExportWaitLine("Packing your files...");
        }

        private void ExportSent(int sent, int count)
        {
            ExportWaitLine(sent < count ? "Sending " + (sent + 1) + " of " + count + "..." : "Almost there...");
            if (exportThing != null) StartCoroutine(FlyPlane(exportThing.position + Vector3.up * .04f));
        }

        private void ExportWaitLine(string line)
        {
            ExportCaption = line;
            Caption(Crew.Fixer, line, float.PositiveInfinity);
        }

        private void EndExportWait(bool ok)
        {
            var body = Body(Crew.Fixer);
            body?.StopWork(false);
            if (ok) body?.Play("React_Nod");
            if (captioned != null && captioned.authoredBubble != null && captioned.authoredBubble.title.text == ExportCaption) HideCaption();
            ExportCaption = "";
            exportThing = null;
        }

        // A folded paper dart (two wings and a keel, both sides drawn).
        private Mesh PlaneMesh()
        {
            if (planeMesh != null) return planeMesh;
            var v = new[]
            {
                new Vector3(0f, 0f, .028f), new Vector3(-.016f, .002f, -.016f), new Vector3(0f, 0f, -.016f),
                new Vector3(.016f, .002f, -.016f), new Vector3(0f, -.007f, -.016f)
            };
            int[] one = { 0, 2, 1, 0, 3, 2, 0, 4, 2 };
            var tris = new int[one.Length * 2];
            for (int i = 0; i < one.Length; i += 3)
            {
                tris[i] = one[i]; tris[i + 1] = one[i + 1]; tris[i + 2] = one[i + 2];
                tris[one.Length + i] = one[i]; tris[one.Length + i + 1] = one[i + 2]; tris[one.Length + i + 2] = one[i + 1];
            }
            planeMesh = new Mesh { name = "Paper Plane", vertices = v, triangles = tris };
            planeMesh.RecalculateNormals();
            planeMesh.RecalculateBounds();
            owned.Add(planeMesh);
            return planeMesh;
        }

        private IEnumerator FlyPlane(Vector3 from)
        {
            ExportPlanes++;
            var plane = Part("Export Plane", PlaneMesh());
            Vector3 away = Head != null ? Vector3.ProjectOnPlane(from - Head.position, Vector3.up) : Vector3.forward;
            if (away.sqrMagnitude < 1e-4f) away = Vector3.forward;
            away = Quaternion.Euler(0f, Random.Range(-35f, 35f), 0f) * away.normalized;
            Vector3 to = from + away * .45f + Vector3.up * .55f;
            ToySounds.Play(ToySounds.Cue.Whoosh, from, .3f, 1.5f);
            Body(Crew.Fixer)?.LookAtPoint(to, 1.4f);
            const float time = 1.4f;
            Vector3 last = from;
            for (float t = 0f; t < time && plane != null; t += Time.deltaTime)
            {
                float k = t / time;
                Vector3 at = Vector3.Lerp(from, to, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * .08f + Vector3.Cross(away, Vector3.up) * Mathf.Sin(k * 9f) * .015f;
                Vector3 dir = at - last;
                if (dir.sqrMagnitude > 1e-8f) plane.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(0f, 0f, Mathf.Sin(k * 9f) * 18f);
                plane.position = at;
                plane.localScale = Vector3.one * Mathf.Lerp(1f, .4f, Mathf.SmoothStep(.6f, 1f, k));
                last = at;
                yield return null;
            }
            if (plane != null) { ToyFx.Puff(plane.position, .006f, 3); Destroy(plane.gameObject); }
        }
    }
}
