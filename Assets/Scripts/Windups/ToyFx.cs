using System.Collections.Generic;
using RoomCapsule;
using UnityEngine;

namespace Windups
{
    // Small pooled toy effects drawn with the toy surface material: cotton puffs, an
    // expanding ring, paper confetti and a comic clap burst (impact lines, a pop star, sparkles). Everything shrinks out instead of fading so it
    // stays opaque (cheap on Quest, no sorting). Also puffs where a character lands,
    // by watching CapsulePetController.LandingCount without touching the characters.
    public sealed class ToyFx : MonoBehaviour
    {
        public static ToyFx Instance { get; private set; }
        public Material material;
        public int SpawnedCount { get; private set; }
        public int ActiveCount { get; private set; }

        private enum Kind { Puff, Ring, Confetti, Streak, Flash, Spark, Shower }

        private sealed class Particle
        {
            public Transform transform;
            public MeshFilter filter;
            public Renderer renderer;
            public Kind kind;
            public float born, life, size;
            public Vector3 velocity, spinAxis, origin, direction;
            public float spin, radius;
            public bool alive;
            public Vector3 shape = Vector3.one; // F82: a shower piece's own proportions (strip, square, dot)
            public float phase, delay;
        }

        private static readonly Color[] ConfettiColors =
        {
            new Color(.95f, .30f, .45f), new Color(.95f, .61f, .10f), new Color(.30f, .78f, .46f),
            new Color(.25f, .55f, 1f), new Color(.94f, .45f, .37f), new Color(1f, .87f, .40f)
        };

        private readonly List<Particle> pool = new List<Particle>();
        private Mesh puffMesh, ringMesh, streakMesh, streakWhiteMesh, starMesh, sparkMesh;
        private static readonly Color ClapYellow = new Color(1f, .84f, .22f), ClapWhite = new Color(1f, .98f, .92f);
        private Mesh[] confettiMeshes;
        // F82 (the user: "for the celebration, confetti like this picture, but in our app's style"): the crew's colours
        // and the toy pastels, as flat paper pieces (strips, squares, dots) that pop up in front of the person and flutter
        // down for a few seconds.
        private static readonly Color[] ShowerColors =
        {
            new Color(.45f, .8f, .52f), new Color(.38f, .58f, .95f), new Color(.98f, .55f, .47f), new Color(1f, .84f, .38f),
            new Color(.74f, .62f, .96f), new Color(.98f, .66f, .78f), new Color(.99f, .97f, .9f), new Color(.36f, .82f, .82f)
        };
        private Mesh[] showerMeshes, showerDots;
        public const int PoolMost = 240;
        private readonly Dictionary<CapsulePetController, int> landings = new Dictionary<CapsulePetController, int>();
        private readonly List<CapsulePetController> petKeys = new List<CapsulePetController>();
        private float nextPetScan;

        private void Awake()
        {
            Instance = this;
            puffMesh = BuildPuff();
            ringMesh = BuildRing();
            streakMesh = BuildStreak(ClapYellow);
            streakWhiteMesh = BuildStreak(ClapWhite);
            starMesh = BuildStar(8, 1f, .45f, ClapWhite, ClapYellow);
            sparkMesh = BuildStar(4, 1f, .3f, ClapYellow, ClapYellow);
            confettiMeshes = new Mesh[ConfettiColors.Length];
            for (int i = 0; i < confettiMeshes.Length; i++) confettiMeshes[i] = BuildChip(ConfettiColors[i]);
            showerMeshes = new Mesh[ShowerColors.Length];
            showerDots = new Mesh[ShowerColors.Length];
            for (int i = 0; i < ShowerColors.Length; i++) { showerMeshes[i] = BuildChip(ShowerColors[i]); showerDots[i] = BuildDot(ShowerColors[i]); }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Destroy(puffMesh);
            Destroy(ringMesh);
            Destroy(streakMesh);
            Destroy(streakWhiteMesh);
            Destroy(starMesh);
            Destroy(sparkMesh);
            if (confettiMeshes != null) foreach (var mesh in confettiMeshes) Destroy(mesh);
            if (showerMeshes != null) foreach (var mesh in showerMeshes) Destroy(mesh);
            if (showerDots != null) foreach (var mesh in showerDots) Destroy(mesh);
        }

        public static void Puff(Vector3 position, float size = .022f, int count = 5)
        {
            if (Instance == null) return;
            for (int i = 0; i < count; i++)
            {
                var p = Instance.Spawn(Kind.Puff, Instance.puffMesh);
                Vector3 offset = Random.insideUnitSphere * size * 1.1f;
                offset.y = Mathf.Abs(offset.y) * .4f;
                p.transform.position = position + offset;
                p.transform.rotation = Random.rotation;
                p.size = size * Random.Range(.65f, 1.15f);
                p.life = Random.Range(.45f, .65f);
                p.velocity = offset.normalized * Random.Range(.04f, .09f) + Vector3.up * Random.Range(.05f, .09f);
            }
        }

        public static void Ring(Vector3 position, Quaternion facing, float radius = .12f)
        {
            if (Instance == null) return;
            var p = Instance.Spawn(Kind.Ring, Instance.ringMesh);
            p.transform.SetPositionAndRotation(position, facing);
            p.size = radius;
            p.life = .5f;
            p.velocity = Vector3.zero;
        }

        public static void Confetti(Vector3 position, int count = 18)
        {
            if (Instance == null) return;
            for (int i = 0; i < count; i++)
            {
                var p = Instance.Spawn(Kind.Confetti, Instance.confettiMeshes[i % Instance.confettiMeshes.Length]);
                p.transform.position = position + Random.insideUnitSphere * .03f;
                p.transform.rotation = Random.rotation;
                p.size = Random.Range(.014f, .02f);
                p.life = Random.Range(.9f, 1.3f);
                Vector3 direction = Random.insideUnitSphere;
                direction.y = Mathf.Abs(direction.y) + .6f;
                p.velocity = direction.normalized * Random.Range(.55f, .95f);
                p.spinAxis = Random.onUnitSphere;
                p.spin = Random.Range(360f, 900f);
            }
        }

        // F82: a celebration shower. Pieces pop up from a line `width` wide at `centre` (facing `toViewer`), then flutter
        // down slowly, swaying and spinning, for 3-4.5 s; a mix of strips, squares and dots in ShowerColors.
        public static void ConfettiShower(Vector3 centre, Vector3 toViewer, float width = .3f, int count = 60)
        {
            if (Instance == null) return;
            if (toViewer.sqrMagnitude < 1e-6f) toViewer = Vector3.back;
            Vector3 flat = Vector3.ProjectOnPlane(toViewer, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.back;
            flat.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, flat);
            ShowersStarted++;
            for (int i = 0; i < count; i++)
            {
                int colour = Random.Range(0, ShowerColors.Length);
                int shape = i % 5; // 0-2 strips, 3 square, 4 dot
                var p = Instance.Spawn(Kind.Shower, shape == 4 ? Instance.showerDots[colour] : Instance.showerMeshes[colour]);
                float across = Random.Range(-.5f, .5f) * width;
                p.transform.position = centre + side * across + flat * Random.Range(-.08f, .08f) + Vector3.up * Random.Range(-.05f, .12f);
                p.transform.rotation = Random.rotation;
                p.size = Random.Range(.017f, .026f);
                p.shape = shape <= 2 ? new Vector3(Random.Range(.7f, .9f), Random.Range(1.5f, 2.1f), .6f) : new Vector3(1f, 1f, .6f);
                p.life = Random.Range(3f, 4.5f);
                p.delay = Random.Range(0f, .25f);
                Vector3 up = Vector3.up * Random.Range(.25f, .75f) + side * Random.Range(-.12f, .12f) + flat * Random.Range(-.05f, .1f) + side * across * .3f;
                p.velocity = up;
                p.direction = (side * Random.Range(-1f, 1f) + flat * Random.Range(-.5f, .5f)).normalized;
                p.spinAxis = Random.onUnitSphere;
                p.spin = Random.Range(220f, 520f);
                p.phase = Random.Range(0f, 6.28f);
                p.origin = p.transform.position;
            }
        }
        public static int ShowersStarted { get; private set; }

        // A clap: comic impact lines shooting out from where the hands met (in the plane facing the
        // person), a quick star pop in the middle and a few sparkles. About a third of a second.
        public static void ClapBurst(Vector3 position, Vector3 toViewer)
        {
            if (Instance == null) return;
            if (toViewer.sqrMagnitude < 1e-6f) toViewer = Vector3.back;
            Vector3 up = Mathf.Abs(Vector3.Dot(toViewer.normalized, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            Quaternion facing = Quaternion.LookRotation(-toViewer.normalized, up);
            const int lines = 10;
            float turn = Random.Range(0f, 36f);
            for (int i = 0; i < lines; i++)
            {
                bool big = i % 2 == 0;
                var p = Instance.Spawn(Kind.Streak, big ? Instance.streakMesh : Instance.streakWhiteMesh);
                Quaternion rotation = facing * Quaternion.Euler(0f, 0f, turn + i * 360f / lines + Random.Range(-7f, 7f));
                p.origin = position;
                p.direction = rotation * Vector3.up;
                p.transform.SetPositionAndRotation(position, rotation);
                p.size = (big ? .05f : .036f) * Random.Range(.9f, 1.1f);   // longest streak length
                p.radius = big ? .026f : .032f;                             // where the lines start
                p.life = Random.Range(.3f, .36f);
            }
            var star = Instance.Spawn(Kind.Flash, Instance.starMesh);
            star.transform.SetPositionAndRotation(position, facing * Quaternion.Euler(0f, 0f, Random.Range(0f, 45f)));
            star.size = .03f;
            star.life = .2f;
            for (int i = 0; i < 5; i++)
            {
                var p = Instance.Spawn(Kind.Spark, Instance.sparkMesh);
                Quaternion rotation = facing * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
                Vector3 outward = rotation * Vector3.up;
                p.transform.SetPositionAndRotation(position + outward * .03f, rotation);
                p.velocity = (outward + Vector3.up * .3f) * Random.Range(.35f, .6f);
                p.spinAxis = facing * Vector3.forward;
                p.spin = Random.Range(-540f, 540f);
                p.size = Random.Range(.008f, .012f);
                p.life = Random.Range(.4f, .55f);
            }
        }

        private Particle Spawn(Kind kind, Mesh mesh)
        {
            Particle particle = null;
            foreach (var candidate in pool) if (!candidate.alive) { particle = candidate; break; }
            if (particle == null)
            {
                if (pool.Count >= PoolMost) particle = pool[SpawnedCount % pool.Count];
                else
                {
                    var go = new GameObject("Toy Fx", typeof(MeshFilter), typeof(MeshRenderer));
                    go.transform.SetParent(transform, false);
                    var renderer = go.GetComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    particle = new Particle { transform = go.transform, filter = go.GetComponent<MeshFilter>(), renderer = renderer };
                    pool.Add(particle);
                }
            }
            particle.kind = kind;
            particle.filter.sharedMesh = mesh;
            // Start the clock on the first LateUpdate, so spawns from outside the player
            // loop (editor tools, async tests) live their full lifetime too.
            particle.born = -1f;
            particle.alive = true;
            particle.transform.gameObject.SetActive(true);
            particle.transform.localScale = Vector3.zero;
            SpawnedCount++;
            return particle;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            int active = 0;
            foreach (var p in pool)
            {
                if (!p.alive) continue;
                if (p.born < 0f) p.born = Time.time;
                float age = (Time.time - p.born) / p.life;
                if (age >= 1f)
                {
                    p.alive = false;
                    p.transform.gameObject.SetActive(false);
                    continue;
                }
                active++;
                switch (p.kind)
                {
                    case Kind.Puff:
                    {
                        // Quick overshoot in, slow shrink out while drifting up.
                        float grow = Mathf.Clamp01(age / .22f);
                        float scale = (1f + .25f * Mathf.Sin(grow * Mathf.PI)) * grow * (1f - Mathf.SmoothStep(.35f, 1f, age));
                        p.transform.localScale = Vector3.one * p.size * scale;
                        p.transform.position += p.velocity * dt;
                        p.velocity *= 1f - 3f * dt;
                        break;
                    }
                    case Kind.Ring:
                    {
                        float spread = Mathf.Lerp(.25f, 1f, 1f - Mathf.Pow(1f - age, 3f));
                        float thickness = 1f - Mathf.SmoothStep(.2f, 1f, age);
                        p.transform.localScale = new Vector3(p.size * spread, p.size * spread, p.size * Mathf.Max(.001f, thickness));
                        break;
                    }
                    case Kind.Streak:
                    {
                        // The outer end shoots out first; the inner end catches up, so each line
                        // stretches and then shrinks to nothing at the far end.
                        float reach = p.size * 1.8f;
                        float outer = p.radius + reach * EaseOut(Mathf.Clamp01(age / .55f));
                        float inner = p.radius + reach * Mathf.Pow(EaseOut(age), 2.2f);
                        p.transform.position = p.origin + p.direction * inner;
                        p.transform.localScale = new Vector3(p.size * .16f, Mathf.Max(.0001f, outer - inner), 1f);
                        break;
                    }
                    case Kind.Flash:
                    {
                        float scale = Mathf.Sin(Mathf.PI * age) * (1f + .35f * (1f - age));
                        p.transform.localScale = Vector3.one * p.size * scale;
                        break;
                    }
                    case Kind.Spark:
                    {
                        p.velocity *= 1f - 4f * dt;
                        p.velocity += Vector3.down * .4f * dt;
                        p.transform.position += p.velocity * dt;
                        p.transform.Rotate(p.spinAxis, p.spin * dt, Space.World);
                        float scale = Mathf.Clamp01(age / .1f) * (1f - Mathf.SmoothStep(.5f, 1f, age));
                        p.transform.localScale = Vector3.one * p.size * scale;
                        break;
                    }
                    case Kind.Shower:
                    {
                        float t = Time.time - p.born;
                        if (t < p.delay) { p.transform.localScale = Vector3.zero; break; }
                        // Up fast, then air drag takes over: a slow, swaying fall (paper's terminal speed).
                        p.velocity += Vector3.down * 2.4f * dt;
                        p.velocity *= 1f - 2.6f * dt;
                        if (p.velocity.y < -.28f) p.velocity.y = -.28f;
                        Vector3 sway = p.direction * Mathf.Sin(t * 3.1f + p.phase) * .07f;
                        p.transform.position += (p.velocity + sway) * dt;
                        p.transform.Rotate(p.spinAxis, p.spin * dt * (p.velocity.y < 0f ? .55f : 1f), Space.World);
                        float grow = Mathf.Clamp01((t - p.delay) / .12f), fade = 1f - Mathf.SmoothStep(.82f, 1f, age);
                        p.transform.localScale = Vector3.Scale(p.shape, Vector3.one * p.size * grow * fade);
                        break;
                    }
                    case Kind.Confetti:
                    {
                        p.velocity += Vector3.down * 2.2f * dt;
                        p.velocity *= 1f - 1.8f * dt;
                        p.transform.position += p.velocity * dt;
                        p.transform.Rotate(p.spinAxis, p.spin * dt, Space.World);
                        float scale = Mathf.Clamp01(age / .08f) * (1f - Mathf.SmoothStep(.7f, 1f, age));
                        p.transform.localScale = Vector3.one * p.size * scale;
                        break;
                    }
                }
            }
            ActiveCount = active;
            WatchLandings();
        }

        private void WatchLandings()
        {
            if (Time.time >= nextPetScan)
            {
                nextPetScan = Time.time + 2f;
                foreach (var pet in FindObjectsByType<CapsulePetController>(FindObjectsSortMode.None))
                    if (!landings.ContainsKey(pet)) landings[pet] = pet.LandingCount;
            }
            petKeys.Clear();
            petKeys.AddRange(landings.Keys);
            foreach (var pet in petKeys)
            {
                if (pet == null) { landings.Remove(pet); continue; }
                if (pet.LandingCount == landings[pet]) continue;
                landings[pet] = pet.LandingCount;
                if (pet.isActiveAndEnabled) Puff(pet.transform.position + Vector3.down * .07f, .016f, 4);
            }
        }

        // Low-poly cotton puff: a subdivided octahedron pushed onto a sphere.
        private static Mesh BuildPuff()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            Vector3[] corners = { Vector3.up, Vector3.forward, Vector3.right, Vector3.back, Vector3.left, Vector3.down };
            int[,] faces = { { 0, 1, 2 }, { 0, 2, 3 }, { 0, 3, 4 }, { 0, 4, 1 }, { 5, 2, 1 }, { 5, 3, 2 }, { 5, 4, 3 }, { 5, 1, 4 } };
            const int steps = 3;
            for (int f = 0; f < 8; f++)
            {
                Vector3 a = corners[faces[f, 0]], b = corners[faces[f, 1]], c = corners[faces[f, 2]];
                int start = vertices.Count;
                for (int i = 0; i <= steps; i++)
                for (int j = 0; j <= steps - i; j++)
                    vertices.Add((a + (b - a) * i / steps + (c - a) * j / steps).normalized * .5f);
                int Row(int i) { int index = 0; for (int r = 0; r < i; r++) index += steps - r + 1; return start + index; }
                for (int i = 0; i < steps; i++)
                for (int j = 0; j < steps - i; j++)
                {
                    int v0 = Row(i) + j, v1 = Row(i + 1) + j, v2 = v0 + 1;
                    triangles.AddRange(new[] { v0, v2, v1 });
                    if (j < steps - i - 1) triangles.AddRange(new[] { v2, v1 + 1, v1 });
                }
            }
            var mesh = new Mesh { name = "ToyPuff" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            var colors = new Color[vertices.Count];
            for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
            mesh.colors = colors;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Flat torus in the XY plane (faces the viewer when the transform faces them).
        private static Mesh BuildRing()
        {
            const int around = 40, tube = 6;
            const float radius = 1f, thickness = .08f;
            var vertices = new Vector3[(around + 1) * (tube + 1)];
            var normals = new Vector3[vertices.Length];
            var colors = new Color[vertices.Length];
            for (int i = 0; i <= around; i++)
            for (int j = 0; j <= tube; j++)
            {
                float u = i / (float)around * Mathf.PI * 2f, v = j / (float)tube * Mathf.PI * 2f;
                var center = new Vector3(Mathf.Cos(u), Mathf.Sin(u), 0f) * radius;
                var normal = new Vector3(Mathf.Cos(u) * Mathf.Cos(v), Mathf.Sin(u) * Mathf.Cos(v), Mathf.Sin(v));
                int index = i * (tube + 1) + j;
                vertices[index] = center + normal * thickness;
                normals[index] = normal;
                colors[index] = Color.white;
            }
            var triangles = new List<int>();
            for (int i = 0; i < around; i++)
            for (int j = 0; j < tube; j++)
            {
                int a = i * (tube + 1) + j, b = a + tube + 1;
                triangles.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
            }
            var mesh = new Mesh { name = "ToyRing" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static float EaseOut(float x) => 1f - (1f - x) * (1f - x) * (1f - x);

        // A flat impact line in the XY plane from y = 0 to y = 1, slightly wider at the far end and
        // rounded at both ends (local x is its width, scaled per particle).
        private static Mesh BuildStreak(Color color)
        {
            const int caps = 5;
            var outline = new List<Vector3>();
            for (int i = 0; i <= caps; i++)
            {
                float a = Mathf.PI * i / caps;               // outer cap, right to left over the top
                outline.Add(new Vector3(Mathf.Cos(a) * .5f, .92f + Mathf.Sin(a) * .08f, 0f));
            }
            for (int i = 0; i <= caps; i++)
            {
                float a = Mathf.PI + Mathf.PI * i / caps;    // inner cap, left to right under the bottom
                outline.Add(new Vector3(Mathf.Cos(a) * .35f, .06f + Mathf.Sin(a) * .06f, 0f));
            }
            var v = new List<Vector3> { new Vector3(0f, .5f, 0f) };
            v.AddRange(outline);
            var tris = new List<int>();
            for (int i = 0; i < outline.Count; i++) tris.AddRange(new[] { 0, 1 + (i + 1) % outline.Count, 1 + i });
            return FlatMesh("ToyClapStreak", v, tris, color, color);
        }

        // A flat star in the XY plane: points at the outer radius, notches at the inner one.
        private static Mesh BuildStar(int points, float outerRadius, float innerRadius, Color middle, Color tips)
        {
            var v = new List<Vector3> { Vector3.zero };
            int ring = points * 2;
            for (int i = 0; i < ring; i++)
            {
                float a = Mathf.PI * 2f * i / ring + Mathf.PI * .5f;
                float r = i % 2 == 0 ? outerRadius : innerRadius;
                v.Add(new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
            }
            var tris = new List<int>();
            for (int i = 0; i < ring; i++) tris.AddRange(new[] { 0, 1 + (i + 1) % ring, 1 + i });
            return FlatMesh("ToyClapStar", v, tris, middle, tips);
        }

        // Flat mesh facing -Z (the toy shader draws both sides and turns normals to the viewer).
        private static Mesh FlatMesh(string name, List<Vector3> vertices, List<int> triangles, Color middle, Color edge)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            var normals = new Vector3[vertices.Count];
            var colors = new Color[vertices.Count];
            for (int i = 0; i < normals.Length; i++) { normals[i] = Vector3.back; colors[i] = i == 0 ? middle : edge; }
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Thin rounded-ish paper chip, coloured through vertex colours.
        // F82: a flat round paper dot (both faces).
        private static Mesh BuildDot(Color color)
        {
            const int sides = 12;
            var v = new List<Vector3> { Vector3.zero };
            var n = new List<Vector3> { Vector3.back };
            var tris = new List<int>();
            for (int i = 0; i < sides; i++) { float a = i * Mathf.PI * 2f / sides; v.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * .5f); n.Add(Vector3.back); }
            for (int i = 0; i < sides; i++) { int a = 1 + i, b = 1 + (i + 1) % sides; tris.AddRange(new[] { 0, a, b, 0, b, a }); }
            var mesh = new Mesh { name = "ToyConfettiDot" };
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            var colors = new Color[v.Count];
            for (int i = 0; i < colors.Length; i++) colors[i] = color;
            mesh.colors = colors;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildChip(Color color)
        {
            var mesh = new Mesh { name = "ToyConfetti" };
            float w = .5f, h = .8f, t = .08f;
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var tris = new List<int>();
            void Face(Vector3 normal, Vector3 right, Vector3 up, float halfR, float halfU, float depth)
            {
                int s = v.Count;
                Vector3 c = normal * depth;
                v.Add(c - right * halfR - up * halfU); v.Add(c + right * halfR - up * halfU);
                v.Add(c - right * halfR + up * halfU); v.Add(c + right * halfR + up * halfU);
                for (int k = 0; k < 4; k++) n.Add(normal);
                tris.AddRange(new[] { s, s + 2, s + 1, s + 1, s + 2, s + 3 });
            }
            Face(Vector3.back, Vector3.left, Vector3.up, w, h, t);
            Face(Vector3.forward, Vector3.right, Vector3.up, w, h, t);
            Face(Vector3.right, Vector3.back, Vector3.up, t, h, w);
            Face(Vector3.left, Vector3.forward, Vector3.up, t, h, w);
            Face(Vector3.up, Vector3.right, Vector3.back, w, t, h);
            Face(Vector3.down, Vector3.right, Vector3.forward, w, t, h);
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            var colors = new Color[v.Count];
            for (int i = 0; i < colors.Length; i++) colors[i] = color;
            mesh.colors = colors;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
