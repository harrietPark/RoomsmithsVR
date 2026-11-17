using System.Collections.Generic;
using UnityEngine;
using Windups;

namespace Windups.Handoff
{
    // F35 (2026-11-12): a sketch page Maker crumpled and tossed over its shoulder. A faceted paper ball (pencil
    // streaks on some facets) that bounces "tok tok" on the desk, rolls and comes to rest; resting, it turns
    // kinematic. The made mesh and physics material are shared by every ball.
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
    public sealed class PaperBall : MonoBehaviour
    {
        public const float Radius = .011f;
        public int Bounces { get; private set; }
        public bool Resting { get; private set; }
        public bool Thrown { get; private set; }

        private Rigidbody body;
        private float lastSound = -1f, stillSince = -1f, thrownAt;

        private static readonly Dictionary<int, Mesh> meshes = new Dictionary<int, Mesh>();
        private static PhysicsMaterial bouncy;

        public static PaperBall Make(Transform parent, Material material, int seed)
        {
            var go = new GameObject("Crumpled Draft", typeof(MeshFilter), typeof(MeshRenderer), typeof(SphereCollider), typeof(Rigidbody));
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * Radius;
            go.GetComponent<MeshFilter>().sharedMesh = Crumpled(seed % 4);
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var sphere = go.GetComponent<SphereCollider>();
            sphere.radius = .9f;
            if (bouncy == null)
                bouncy = new PhysicsMaterial("Paper Ball") { bounciness = .45f, dynamicFriction = .5f, staticFriction = .6f, bounceCombine = PhysicsMaterialCombine.Maximum };
            sphere.sharedMaterial = bouncy;
            var body = go.GetComponent<Rigidbody>();
            body.mass = .01f;
            body.linearDamping = .15f;
            body.angularDamping = .8f;
            body.isKinematic = true;
            body.useGravity = false;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            return go.AddComponent<PaperBall>();
        }

        private void Awake() => body = GetComponent<Rigidbody>();

        public void Throw(Vector3 velocity, Vector3 spin)
        {
            Thrown = true;
            thrownAt = Time.time;
            body.isKinematic = false;
            body.useGravity = true;
            body.linearVelocity = velocity;
            body.angularVelocity = spin;
        }

        // F56: picked up (a crumpled shape card in the hand): no physics until it is thrown or dropped again.
        public void Hold()
        {
            if (body == null) body = GetComponent<Rigidbody>();
            Thrown = false;
            Resting = false;
            Bounces = 0;
            stillSince = -1f;
            body.isKinematic = true;
            body.useGravity = false;
            body.linearDamping = .15f;
            body.angularDamping = .8f;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!Thrown || Resting) return;
            float speed = collision.relativeVelocity.magnitude;
            if (speed < .12f || Time.time - lastSound < .07f) return;
            lastSound = Time.time;
            Bounces++;
            // A light papery "tok": higher and quieter as it settles.
            ToySounds.Play(ToySounds.Cue.Press, transform.position, Mathf.Clamp(speed * .35f, .08f, .4f), 2.1f + Mathf.Min(Bounces, 4) * .08f);
            if (Bounces == 1) ToySounds.Play(ToySounds.Cue.Paper, transform.position, .18f, 1.8f);
            // Crumpled paper does not roll far: after the first bounce it slows down quickly (it piles up behind
            // Maker instead of rolling across the room).
            body.linearDamping = 2.6f;
            body.angularDamping = 3.5f;
        }

        private void FixedUpdate()
        {
            if (!Thrown || Resting) return;
            if (transform.position.y < -3f) { Destroy(gameObject); return; }
            // Still for a moment (or rolling for too long): it rests and stops costing physics.
            if (body.linearVelocity.magnitude < .03f && body.angularVelocity.magnitude < 1.2f)
            {
                if (stillSince < 0f) stillSince = Time.time;
                else if (Time.time - stillSince > .4f) Rest();
            }
            else stillSince = -1f;
            if (Time.time - thrownAt > 8f) Rest();
        }

        private void Rest()
        {
            Resting = true;
            body.isKinematic = true;
        }

        // ------------------------------------------------------------ the crumpled mesh

        // A once-subdivided icosahedron (80 facets) pushed in and out at random, flat shaded: paper white with a
        // little shade per facet, and a pencil streak (one graphite corner) on about one facet in six.
        private static Mesh Crumpled(int variant)
        {
            if (meshes.TryGetValue(variant, out var made) && made != null) return made;
            float t = (1f + Mathf.Sqrt(5f)) * .5f;
            var v = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1)
            };
            for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized;
            int[] faces =
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1
            };
            var mid = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (mid.TryGetValue(key, out int m)) return m;
                v.Add(((v[a] + v[b]) * .5f).normalized);
                mid[key] = v.Count - 1;
                return v.Count - 1;
            }
            var tris = new List<int>();
            for (int i = 0; i < faces.Length; i += 3)
            {
                int a = faces[i], b = faces[i + 1], c = faces[i + 2];
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                tris.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }
            var random = new System.Random(31 + variant * 7);
            for (int i = 0; i < v.Count; i++) v[i] *= .76f + (float)random.NextDouble() * .34f;
            var verts = new List<Vector3>();
            var colors = new List<Color>();
            var index = new List<int>();
            Color paper = new Color(.97f, .96f, .92f), graphite = new Color(.4f, .39f, .38f);
            for (int i = 0; i < tris.Count; i += 3)
            {
                Vector3 a = v[tris[i]], b = v[tris[i + 1]], c = v[tris[i + 2]];
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), a + b + c) < 0f) { var s = b; b = c; c = s; }
                float shade = .86f + (float)random.NextDouble() * .14f;
                bool streak = random.NextDouble() < .17;
                int n = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c);
                for (int k = 0; k < 3; k++) colors.Add(streak && k == 0 ? graphite : new Color(paper.r * shade, paper.g * shade, paper.b * shade, 1f));
                index.Add(n); index.Add(n + 1); index.Add(n + 2);
            }
            made = new Mesh { name = "CrumpledPaper" + variant };
            made.SetVertices(verts);
            made.SetColors(colors);
            made.SetTriangles(index, 0);
            made.RecalculateNormals();
            made.RecalculateBounds();
            meshes[variant] = made;
            return made;
        }
    }
}
