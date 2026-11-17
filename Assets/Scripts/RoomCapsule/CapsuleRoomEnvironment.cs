using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Text = UnityEngine.UI.Text;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace RoomCapsule
{
    public sealed class CapsuleRoomEnvironment : MonoBehaviour
    {
        public const float Radius = 0.04f;
        public const float Height = 0.15f;
        public const float MaxClimb = 1.2f;
        public const float MaxDrop = 3f;
        public const MRUKAnchor.SceneLabels FurnitureLabels = MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.COUCH |
            MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.STORAGE;
        public const MRUKAnchor.SceneLabels WalkableLabels = FurnitureLabels | MRUKAnchor.SceneLabels.FLOOR;

        [SerializeField] private MRUK mruk;
        [SerializeField] private EffectMesh effectMesh;
        [SerializeField] private GameObject editorRoomPrefab;
        [SerializeField] private int agentTypeId;
        [SerializeField] private int geometryLayer;
        [SerializeField] private Text statusLabel;

        public bool IsReady { get; private set; }
        public string Status { get; private set; } = "Waiting for room data";
        public MRUKRoom Room { get; private set; }
        public int Revision { get; private set; }
        public int RejectedEdges { get; private set; }
        public int GeometryMask => 1 << geometryLayer;
        public int AgentTypeId => agentTypeId;
        public readonly List<CapsuleRoomSurface> Surfaces = new List<CapsuleRoomSurface>();
        public readonly List<CapsuleTraversalLink> Links = new List<CapsuleTraversalLink>();
        public event Action NavigationChanging = delegate { };
        public event Action NavigationReady = delegate { };

        private readonly Dictionary<Collider, MRUKAnchor> anchorsByCollider = new Dictionary<Collider, MRUKAnchor>();
        private NavMeshData navData;
        private NavMeshDataInstance navInstance;
        private Coroutine rebuild;
        private bool loading;
        private bool shuttingDown;
        private NavMeshTriangulation triangulation;
        private float[] triangleAreas;
        private float totalArea;
        public NavMeshQueryFilter Query => new NavMeshQueryFilter { agentTypeID = agentTypeId, areaMask = NavMesh.AllAreas };

        private IEnumerator Start()
        {
            if (mruk == null || effectMesh == null) { SetStatus("MRUK configuration missing"); yield break; }
            effectMesh.HideMesh = !Application.isEditor;
            mruk.SceneLoadedEvent.AddListener(RoomLoaded);
            mruk.RoomCreatedEvent.AddListener(RoomChanged);
            mruk.RoomUpdatedEvent.AddListener(RoomChanged);
            mruk.RoomRemovedEvent.AddListener(RoomChanged);
            yield return LoadRoom();
        }

        public void ReloadRoom() { if (!loading) StartCoroutine(LoadRoom()); }

        // F78 airplane mode: with no internet the room scan is not loaded at all (it may not exist, e.g. on a plane, and
        // asking for Space Setup there would stop the app); the crew stands on the title sign instead.
        public bool Roomless { get; private set; }
        public const float NetworkWait = 3.5f;

        private IEnumerator LoadRoom()
        {
            if (Windups.WindupsNetwork.Started)
            {
                float until = Time.realtimeSinceStartup + NetworkWait;
                while (!Windups.WindupsNetwork.Known && Time.realtimeSinceStartup < until) yield return null;
                if (Windups.WindupsNetwork.Known && Windups.WindupsNetwork.Offline)
                {
                    Roomless = true;
                    SetStatus("No internet: collecting without the room.");
                    Debug.Log("[Capsule Room] airplane mode: the room scan is not loaded");
                    yield break;
                }
            }
            loading = true;
            SuspendNavigation();
            SetStatus(Application.isEditor ? "Loading MRUK sample room (Editor)" : "Loading scanned room...");
#if UNITY_ANDROID && !UNITY_EDITOR
            // The space permission was asked with the others at start; without it the room can't load, so
            // a "no" is asked again (a few times) before giving up.
            for (int tries = 0; tries < 3 && !Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission); tries++)
            {
                SetStatus("Allow room access to start");
                if (tries > 0) yield return new WaitForSeconds(1.5f);
                yield return Windups.WindupsPermissions.Ensure(OVRPermissionsRequester.ScenePermission, null);
                if (Windups.WindupsPermissions.Blocked(OVRPermissionsRequester.ScenePermission)) break;
            }
            if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
            { SetStatus("Room access denied. Allow it in app permissions, then restart the app."); loading = false; yield break; }
#endif
            Task<MRUK.LoadDeviceResult> task;
            try
            {
#if UNITY_EDITOR
                if (editorRoomPrefab == null) throw new InvalidOperationException("Assign an MRUK sample room prefab.");
                task = mruk.LoadSceneFromPrefab(editorRoomPrefab);
#else
                task = mruk.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: true);
#endif
            }
            catch (Exception e) { SetStatus("Room load failed: " + e.Message); loading = false; yield break; }
            while (!task.IsCompleted) yield return null;
            loading = false;
            if (task.IsFaulted || task.IsCanceled) { SetStatus("Room load failed. Scan your space, then restart the app."); yield break; }
            if (task.Result != MRUK.LoadDeviceResult.Success)
            {
                SetStatus("Room unavailable: " + task.Result + ". Finish Space Setup, then restart the app.");
                yield break;
            }
            RoomLoaded();
        }

        private void RoomLoaded()
        {
            var next = mruk.GetCurrentRoom();
            if (Room != next)
            {
                UnsubscribeRoom();
                Room = next;
                if (Room != null)
                {
                    Room.AnchorCreatedEvent.AddListener(AnchorChanged);
                    Room.AnchorUpdatedEvent.AddListener(AnchorChanged);
                    Room.AnchorRemovedEvent.AddListener(AnchorChanged);
                }
            }
            ScheduleRebuild();
        }
        private void RoomChanged(MRUKRoom _) => RoomLoaded();
        private void AnchorChanged(MRUKAnchor _) => ScheduleRebuild();
        private void ScheduleRebuild()
        {
            if (shuttingDown) return;
            SuspendNavigation();
            if (rebuild != null) StopCoroutine(rebuild);
            rebuild = StartCoroutine(Rebuild());
        }
        private void SuspendNavigation()
        {
            IsReady = false;
            NavigationChanging();
        }

        private IEnumerator Rebuild()
        {
            SetStatus("Building room navigation...");
            yield return new WaitForSecondsRealtime(0.25f);
            ClearNavigation();
            effectMesh.DestroyMesh();
            yield return null; // EffectMesh uses deferred Destroy; never bake stale colliders.
            if (Room == null) { SetStatus("No scanned room. Finish Space Setup, then restart the app."); rebuild = null; yield break; }
            effectMesh.CreateMesh(Room);
            Physics.SyncTransforms();
            anchorsByCollider.Clear();
            Surfaces.Clear();
            var sources = new List<NavMeshBuildSource>();
            var bounds = new Bounds();
            bool firstPoint = true;
            foreach (var pair in effectMesh.EffectMeshObjects)
            {
                var anchor = pair.Key;
                var generated = pair.Value;
                if (anchor == null || anchor.Room != Room || generated.mesh == null || generated.collider == null) continue;
                anchorsByCollider[generated.collider] = anchor;
                bool walkable = anchor.HasAnyLabel(WalkableLabels) && !anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE |
                    MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE | MRUKAnchor.SceneLabels.INNER_WALL_FACE | MRUKAnchor.SceneLabels.CEILING);
                sources.Add(new NavMeshBuildSource { shape = NavMeshBuildSourceShape.Mesh, sourceObject = generated.mesh,
                    transform = generated.effectMeshGO.transform.localToWorldMatrix, area = walkable ? 0 : 1 });
                if (generated.collider is BoxCollider box) AddSolidInterior(box, walkable, sources);
                foreach (var local in generated.mesh.vertices)
                {
                    var world = generated.effectMeshGO.transform.TransformPoint(local);
                    if (firstPoint) { bounds = new Bounds(world, Vector3.zero); firstPoint = false; }
                    else bounds.Encapsulate(world);
                }
                if (walkable && TryGetTop(anchor, generated, out var surface)) Surfaces.Add(surface);
            }
            var settings = NavMesh.GetSettingsByID(agentTypeId);
            if (sources.Count == 0 || settings.agentTypeID != agentTypeId || !Surfaces.Exists(s => s.IsFloor))
            {
                SetStatus("No usable floor in room data. Rescan your room, then restart the app."); rebuild = null; yield break;
            }
            settings.agentRadius = Radius;
            settings.agentHeight = Height;
            settings.agentClimb = 0.02f;
            settings.agentSlope = 20f;
            settings.overrideVoxelSize = true;
            settings.voxelSize = Radius / 3f;
            settings.minRegionArea = 0.002f;
            bounds.Expand(0.5f);
            navData = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
            if (navData == null) { SetStatus("Room navigation build failed"); rebuild = null; yield break; }
            navData.name = "Runtime Capsule Room Navigation";
            navInstance = NavMesh.AddNavMeshData(navData);
            BuildLinks();
            triangulation = NavMesh.CalculateTriangulation();
            BuildTriangleWeights();
            IsReady = totalArea > 0;
            Revision++;
            SetStatus(IsReady ? (Application.isEditor ? "EDITOR SAMPLE ROOM" : "SCANNED ROOM") +
                $" | {Links.Count} directed furniture links | Grab capsule to pause" : "No walkable room area found");
            rebuild = null;
            if (IsReady) NavigationReady();
        }

        private static void AddSolidInterior(BoxCollider box, bool preserveWalkableTop, List<NavMeshBuildSource> sources)
        {
            // A triangle shell alone can leave a disconnected floor island inside a furniture volume.
            // Fill the volume, leaving the original walkable top above the non-walkable interior.
            var up = box.transform.InverseTransformDirection(Vector3.up).normalized;
            int axis = Mathf.Abs(up.x) > Mathf.Abs(up.y) ? 0 : 1;
            if (Mathf.Abs(up.z) > Mathf.Abs(up[axis])) axis = 2;
            if (Mathf.Abs(up[axis]) < 0.95f) return;
            var size = box.size;
            if (preserveWalkableTop && size[axis] < 0.06f) return;
            var center = box.center;
            if (preserveWalkableTop)
            {
                size[axis] -= 0.04f;
                center[axis] -= Mathf.Sign(up[axis]) * 0.02f;
            }
            sources.Add(new NavMeshBuildSource { shape = NavMeshBuildSourceShape.Box, size = size,
                transform = box.transform.localToWorldMatrix * Matrix4x4.Translate(center), area = 1 });
        }

        private static bool TryGetTop(MRUKAnchor anchor, EffectMesh.EffectMeshObject generated, out CapsuleRoomSurface surface)
        {
            surface = null;
            var vertices = generated.mesh.vertices;
            var triangles = generated.mesh.triangles;
            var transform = generated.effectMeshGO.transform;
            var topVertices = new List<Vector3>();
            float highest = float.NegativeInfinity;
            Vector3 normal = Vector3.up, origin = Vector3.zero;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                var a = transform.TransformPoint(vertices[triangles[i]]);
                var b = transform.TransformPoint(vertices[triangles[i + 1]]);
                var c = transform.TransformPoint(vertices[triangles[i + 2]]);
                var n = Vector3.Cross(b - a, c - a).normalized;
                if (Vector3.Dot(n, Vector3.up) < Mathf.Cos(20f * Mathf.Deg2Rad)) continue;
                float y = (a.y + b.y + c.y) / 3f;
                if (y > highest + 0.025f) { topVertices.Clear(); highest = y; normal = n; origin = a; }
                if (Mathf.Abs(y - highest) < 0.025f) { topVertices.Add(a); topVertices.Add(b); topVertices.Add(c); }
            }
            if (topVertices.Count < 3) return false;
            var points = new List<Vector2>();
            foreach (var p in topVertices) points.Add(new Vector2(p.x, p.z));
            surface = new CapsuleRoomSurface { Anchor = anchor, Polygon = CapsuleRoomSurface.ConvexHull(points), Origin = origin,
                Normal = normal, IsFloor = anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR) };
            return surface.Polygon.Length >= 3;
        }

        // Up to RoutesPerEdge routes along each furniture edge, at least RouteSpacing apart, so a character on
        // the floor finds a way up near where it is (2026-11-11: one route per edge left characters stranded).
        public const int RoutesPerEdge = 3;
        public const float RouteSpacing = 0.35f;

        private void BuildLinks()
        {
            RejectedEdges = 0;
            foreach (var surface in Surfaces)
            {
                if (surface.IsFloor || !surface.Anchor.HasAnyLabel(FurnitureLabels)) continue;
                var polygon = surface.Polygon;
                for (int i = 0; i < polygon.Length; i++)
                {
                    var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
                    var edge = (b - a).normalized;
                    var outward = new Vector2(edge.y, -edge.x);
                    int samples = Mathf.Clamp(Mathf.FloorToInt(Vector2.Distance(a, b) / 0.3f), 1, 5);
                    var made = new List<Vector2>();
                    for (int sample = 0; sample < samples && made.Count < RoutesPerEdge; sample++)
                    {
                        var middle = Vector2.Lerp(a, b, (sample + 0.5f) / samples);
                        if (made.Exists(m => Vector2.Distance(m, middle) < RouteSpacing)) continue;
                        if (TryCreateRoute(surface, middle, outward)) made.Add(middle);
                        else RejectedEdges++;
                    }
                }
            }
        }

        // ------------------------------------------------------------ jumping up
        // A toy-like jump: lighter gravity than real (a 0.75 m desk takes about 0.65 s), the top of the arc
        // a few centimetres above the furniture top so it clears the edge.
        public const float JumpGravity = 6.5f;
        public static readonly float[] JumpApex = { 0.07f, 0.11f };

        public static Vector3 JumpPoint(Vector3 from, Vector3 to, float apexExtra, float t01)
        {
            float rise = to.y - from.y, apex = Mathf.Max(rise, 0f) + apexExtra;
            float up = Mathf.Sqrt(2f * JumpGravity * apex);
            float time = JumpTime(from, to, apexExtra), t = t01 * time;
            return from + Vector3.ProjectOnPlane(to - from, Vector3.up) * t01 + Vector3.up * (up * t - 0.5f * JumpGravity * t * t);
        }

        public static float JumpTime(Vector3 from, Vector3 to, float apexExtra)
        {
            float rise = to.y - from.y, apex = Mathf.Max(rise, 0f) + apexExtra;
            return Mathf.Sqrt(2f * apex / JumpGravity) + Mathf.Sqrt(2f * (apex - rise) / JumpGravity);
        }

        private bool JumpIsClear(Vector3 from, Vector3 to, float apexExtra)
        {
            var arc = new Vector3[16];
            for (int i = 0; i < arc.Length; i++) arc[i] = JumpPoint(from, to, apexExtra, i / (arc.Length - 1f));
            return PathIsClear(arc);
        }

        // A take-off point on the floor in front of the edge (far enough out for the arc to clear the edge,
        // reachable on foot from the foot of the edge) and an arc that hits nothing.
        private bool TryJumpRoute(CapsuleRoomSurface surface, Vector2 edge, Vector2 outward, Vector3 top, Vector3 bottom,
            MRUKAnchor lowerAnchor, out Vector3 takeoff, out float apexExtra)
        {
            takeoff = default; apexExtra = 0f;
            float rise = top.y - bottom.y;
            float baseOut = Mathf.Clamp(0.1f + rise * 0.2f, 0.12f, 0.3f);
            var path = new NavMeshPath();
            foreach (float outBy in new[] { baseOut, baseOut + 0.06f, baseOut - 0.04f })
            {
                var flat = edge + outward * outBy;
                var probe = new Vector3(flat.x, top.y + 0.01f, flat.y);
                if (!TrySupport(probe, MaxDrop + 0.08f, out var hit, out var anchor) || anchor != lowerAnchor || Mathf.Abs(hit.point.y - bottom.y) > 0.03f) continue;
                if (!TrySameLevelNavPoint(hit.point, out var nav) || !Room.IsPositionInRoom(nav, false)) continue;
                if (!NavMesh.CalculatePath(bottom, nav, Query, path) || path.status != NavMeshPathStatus.PathComplete) continue;
                float length = 0f;
                for (int i = 1; i < path.corners.Length; i++) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                if (length > outBy + 0.2f) continue;
                foreach (float apex in JumpApex)
                {
                    if (!JumpIsClear(nav, top, apex)) continue;
                    takeoff = nav; apexExtra = apex;
                    return true;
                }
            }
            return false;
        }

        private bool TryCreateRoute(CapsuleRoomSurface surface, Vector2 edge, Vector2 outward)
        {
            Vector2 top2D = edge - outward * (Radius + 0.045f);
            if (!surface.Contains(top2D, Radius + 0.015f)) return false;
            var top = surface.Point(top2D);
            if (!TrySameLevelNavPoint(top, out top)) return false;
            var outside = surface.Point(edge + outward * (Radius + 0.05f));
            if (!TrySupport(outside + Vector3.up * 0.01f, MaxDrop + 0.08f, out var lower, out var lowerAnchor)) return false;
            if (lowerAnchor == surface.Anchor || !lowerAnchor.HasAnyLabel(WalkableLabels)) return false;
            float rise = top.y - lower.point.y;
            if (rise < 0.05f || rise > MaxDrop) return false;
            if (!TrySameLevelNavPoint(lower.point, out var bottom)) return false;
            if (!Room.IsPositionInRoom(bottom, false)) return false;
            var upperOutside = new Vector3(outside.x, Mathf.Max(top.y, outside.y) + 0.008f, outside.z);
            var lowerOutside = new Vector3(outside.x, bottom.y, outside.z);
            var climb = new[] { bottom, lowerOutside, upperOutside, top };
            if (!PathIsClear(climb) || !PathIsClear(new[] { top, upperOutside, lowerOutside, bottom })) return false;
            // Up: one jump from a take-off point in front of the edge; the old straight climb only where no
            // jump fits (a shelf over the edge, a narrow gap). Down: a hop off the edge (see the controller).
            if (rise <= MaxClimb && TryJumpRoute(surface, edge, outward, top, bottom, lowerAnchor, out var takeoff, out float apex))
            {
                CreateLink(CapsuleTraversalKind.Climb, surface.Name, new[] { takeoff, top }, true, apex);
                CreateLink(CapsuleTraversalKind.Drop, surface.Name, new[] { top, upperOutside, takeoff });
                return true;
            }
            if (rise <= MaxClimb) CreateLink(CapsuleTraversalKind.Climb, surface.Name, climb);
            CreateLink(CapsuleTraversalKind.Drop, surface.Name, new[] { top, upperOutside, bottom });
            return true;
        }

        private void CreateLink(CapsuleTraversalKind kind, string furniture, Vector3[] waypoints, bool jump = false, float apex = 0f)
        {
            var go = new GameObject((jump ? "Jump " : kind + " ") + furniture);
            go.transform.SetParent(transform, false);
            var metadata = go.AddComponent<CapsuleTraversalLink>();
            metadata.Kind = kind; metadata.Furniture = furniture; metadata.Waypoints = waypoints;
            metadata.Jump = jump; metadata.Apex = apex;
            var link = go.AddComponent<NavMeshLink>();
            link.agentTypeID = agentTypeId;
            link.startPoint = go.transform.InverseTransformPoint(metadata.Start);
            link.endPoint = go.transform.InverseTransformPoint(metadata.End);
            link.bidirectional = false;
            link.width = 0;
            link.costModifier = kind == CapsuleTraversalKind.Climb ? 2f : 1.2f;
            Links.Add(metadata);
        }

        public bool PathIsClear(IReadOnlyList<Vector3> points)
        {
            for (int i = 0; i < points.Count; i++)
            {
                var feet = points[i] + Vector3.up * 0.008f;
                var bottom = feet + Vector3.up * Radius;
                var top = feet + Vector3.up * (Height - Radius);
                if (Physics.CheckCapsule(bottom, top, Radius - 0.003f, GeometryMask, QueryTriggerInteraction.Ignore)) return false;
                if (i == points.Count - 1) continue;
                var delta = points[i + 1] - points[i];
                if (delta.sqrMagnitude > 0.000001f && Physics.CapsuleCast(bottom, top, Radius - 0.003f,
                    delta.normalized, delta.magnitude, GeometryMask, QueryTriggerInteraction.Ignore)) return false;
            }
            return true;
        }

        public bool TrySupport(Vector3 feet, float maxDistance, out RaycastHit hit, out MRUKAnchor anchor)
        {
            anchor = null;
            if (!Physics.Raycast(feet + Vector3.up * 0.025f, Vector3.down, out hit, maxDistance + 0.025f,
                GeometryMask, QueryTriggerInteraction.Ignore) || hit.normal.y < Mathf.Cos(20f * Mathf.Deg2Rad)) return false;
            return anchorsByCollider.TryGetValue(hit.collider, out anchor);
        }

        public bool TrySameLevelNavPoint(Vector3 feet, out Vector3 result)
        {
            result = feet;
            if (!NavMesh.SamplePosition(feet, out var hit, 0.11f, Query) || Mathf.Abs(hit.position.y - feet.y) > 0.04f) return false;
            if (!TrySupport(hit.position, 0.08f, out var support, out var anchor) ||
                !anchor.HasAnyLabel(WalkableLabels) || Mathf.Abs(support.point.y - feet.y) > 0.04f) return false;
            if (!PathIsClear(new[] { hit.position })) return false;
            result = hit.position;
            return true;
        }

        private void BuildTriangleWeights()
        {
            triangleAreas = new float[triangulation.indices.Length / 3];
            totalArea = 0;
            for (int i = 0; i < triangleAreas.Length; i++)
            {
                var a = triangulation.vertices[triangulation.indices[i * 3]];
                var b = triangulation.vertices[triangulation.indices[i * 3 + 1]];
                var c = triangulation.vertices[triangulation.indices[i * 3 + 2]];
                totalArea += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                triangleAreas[i] = totalArea;
            }
        }

        public bool TryRandomPoint(bool floorOnly, out Vector3 point)
        {
            point = default;
            if (totalArea <= 0) return false;
            for (int attempt = 0; attempt < 64; attempt++)
            {
                int triangle = Array.BinarySearch(triangleAreas, UnityEngine.Random.value * totalArea);
                if (triangle < 0) triangle = ~triangle;
                triangle = Mathf.Min(triangle, triangleAreas.Length - 1);
                var a = triangulation.vertices[triangulation.indices[triangle * 3]];
                var b = triangulation.vertices[triangulation.indices[triangle * 3 + 1]];
                var c = triangulation.vertices[triangulation.indices[triangle * 3 + 2]];
                float u = UnityEngine.Random.value, v = UnityEngine.Random.value;
                if (u + v > 1) { u = 1 - u; v = 1 - v; }
                var candidate = a + u * (b - a) + v * (c - a);
                if (!Room.IsPositionInRoom(candidate, false) || !TrySameLevelNavPoint(candidate, out candidate)) continue;
                if (!TrySupport(candidate, 0.08f, out _, out var anchor) ||
                    (floorOnly && !anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR))) continue;
                point = candidate; return true;
            }
            return false;
        }

        // A route that just failed a character (blocked, fell off it) costs a lot for a while, so the next
        // plan takes another way up when there is one. The only way still works.
        public void Avoid(CapsuleTraversalLink link, float seconds)
        {
            if (link == null) return;
            var nav = link.GetComponent<NavMeshLink>();
            if (nav == null) return;
            nav.costModifier = 60f;
            avoidedUntil[link] = Time.time + seconds;
        }
        private readonly Dictionary<CapsuleTraversalLink, float> avoidedUntil = new Dictionary<CapsuleTraversalLink, float>();
        private readonly List<CapsuleTraversalLink> avoidExpired = new List<CapsuleTraversalLink>();
        private void Update()
        {
            if (avoidedUntil.Count == 0) return;
            avoidExpired.Clear();
            foreach (var pair in avoidedUntil) if (pair.Key == null || Time.time >= pair.Value) avoidExpired.Add(pair.Key);
            foreach (var link in avoidExpired)
            {
                avoidedUntil.Remove(link);
                if (link == null) continue;
                var nav = link.GetComponent<NavMeshLink>();
                if (nav != null) nav.costModifier = link.Kind == CapsuleTraversalKind.Climb ? 2f : 1.2f;
            }
        }

        public CapsuleTraversalLink FindLink(OffMeshLinkData data)
        {
            CapsuleTraversalLink best = null;
            float score = 0.01f;
            foreach (var link in Links)
            {
                float d = (link.Start - data.startPos).sqrMagnitude + (link.End - data.endPos).sqrMagnitude;
                if (d < score) { best = link; score = d; }
            }
            return best;
        }
        public void SetStatus(string text) { Status = text; if (statusLabel != null) statusLabel.text = text; Debug.Log("[Windups Room] "+text); }
        private void ClearNavigation()
        {
            foreach (var link in Links) if (link != null)
            {
                link.GetComponent<NavMeshLink>().enabled = false;
                Destroy(link.gameObject);
            }
            Links.Clear();
            avoidedUntil.Clear();
            if (navInstance.valid) navInstance.Remove();
            if (navData != null) Destroy(navData);
            navData = null;
            totalArea = 0;
        }
        private void UnsubscribeRoom()
        {
            if (Room == null) return;
            Room.AnchorCreatedEvent.RemoveListener(AnchorChanged);
            Room.AnchorUpdatedEvent.RemoveListener(AnchorChanged);
            Room.AnchorRemovedEvent.RemoveListener(AnchorChanged);
        }
        private void OnDestroy()
        {
            shuttingDown = true;
            UnsubscribeRoom();
            if (mruk != null)
            {
                mruk.SceneLoadedEvent.RemoveListener(RoomLoaded);
                mruk.RoomCreatedEvent.RemoveListener(RoomChanged);
                mruk.RoomUpdatedEvent.RemoveListener(RoomChanged);
                mruk.RoomRemovedEvent.RemoveListener(RoomChanged);
            }
            ClearNavigation();
        }
    }
}
