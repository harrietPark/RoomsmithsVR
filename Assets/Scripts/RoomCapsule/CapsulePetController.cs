using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using UnityEngine;
using UnityEngine.AI;
using Text = UnityEngine.UI.Text;

namespace RoomCapsule
{
    [RequireComponent(typeof(Rigidbody), typeof(NavMeshAgent), typeof(Grabbable))]
    public sealed class CapsulePetController : MonoBehaviour
    {
        public enum MotionState { WaitingForRoom, Idle, Walking, Climbing, Held, Falling, Recovering }
        [SerializeField] private CapsuleRoomEnvironment environment;
        [SerializeField] private Text stateLabel;
        [SerializeField] private float walkingSpeed = 0.2f;
        [SerializeField] private float climbingSpeed = 0.25f;

        public MotionState State { get; private set; } = MotionState.WaitingForRoom;
        public int ClimbCount { get; private set; }
        public int DropCount { get; private set; }
        public int GrabCount { get; private set; }
        public int LandingCount { get; private set; }
        public int RecoveryCount { get; private set; }
        public Vector3 Destination { get; private set; }
        // Held: in a hand's grab, or standing on the person's open palm (F51). Either way nothing walks it,
        // seats it or calls it away.
        public bool IsHeld => Grabbed || OnPalm;
        public bool Grabbed => grabbable != null && grabbable.GrabPoints.Count > 0;
        public bool RoamingEnabled { get; set; } = true;
        public bool InteractionPaused { get; set; }
        public bool MeetingSeatLocked { get; private set; }
        // Picked up from its meeting seat: once it has landed where it was let go, it sits there again.
        public bool ReseatAfterDrop { get; private set; }

        // A seated character keeps still (kinematic, no navigation, no roaming). Its capsule turns into a
        // trigger, so nothing bumps into it but hands can still grab it (near grab and the far pinch).
        public void LockMeetingSeat()
        {
            if (IsHeld || State != MotionState.Idle) return;
            continueAfterTraversal = false;
            MeetingSeatLocked = true;
            RoamingEnabled = false;
            DisableAgent();
            body.isKinematic = true;
            body.useGravity = false;
            capsule.isTrigger = true;
            SetBlocker(true);
        }

        // F89 (maker): a seated character carried along with what it works on (its seat goes with it; it stays seated).
        public void MoveSeated(Vector3 feet, Quaternion rotation)
        {
            if (!MeetingSeatLocked || IsHeld) return;
            transform.SetPositionAndRotation(feet, rotation);
            body.position = feet;
            body.rotation = rotation;
        }

        public void UnlockMeetingSeat()
        {
            if (!MeetingSeatLocked) return;
            MeetingSeatLocked = false;
            ReseatAfterDrop = false;
            capsule.isTrigger = false;
            SetBlocker(false);
            if (environment.IsReady && !TryResumeWalking()) BeginFalling(Vector3.zero);
        }

        // ------------------------------------------------------------ F51: walking round the seated ones
        // A seated character has no navigation agent, so the others used to walk straight through it. Seated,
        // it now carries a (non-carving) navigation obstacle: walkers steer round it. The navigation mesh is
        // not cut, so seats, sample points and paths stay exactly as they were. A walker that makes no
        // headway for StuckBeforeSqueeze (a desk strip too narrow to pass) is let through, as before.
        public static bool SeatBlockers = true;
        // (0.07 + the walker's own 0.04: the two pass 11 cm apart, middle to middle; the model with its mittens is
        // 11-12 cm wide, so the bodies no longer go through each other.)
        public const float BlockerRadius = 0.07f, StuckBeforeSqueeze = 1.2f, SqueezeTime = 2.5f, SqueezeReach = 0.27f;
        public static int Squeezes { get; private set; }
        private static readonly List<NavMeshObstacle> blockers = new List<NavMeshObstacle>();
        private static readonly Dictionary<NavMeshObstacle, float> letThroughUntil = new Dictionary<NavMeshObstacle, float>();
        private NavMeshObstacle blocker;
        private bool blockerWanted;

        // Other things the crew walks round (the session's made things): the same obstacle, the same squeeze.
        public static NavMeshObstacle AddBlocker(GameObject on)
        {
            var obstacle = on.AddComponent<NavMeshObstacle>();
            obstacle.carving = false;
            obstacle.enabled = false;
            blockers.Add(obstacle);
            return obstacle;
        }

        public static bool LetThrough(NavMeshObstacle obstacle) => letThroughUntil.TryGetValue(obstacle, out float until) && Time.time < until;

        private void SetBlocker(bool on)
        {
            blockerWanted = on;
            if (blocker != null) blocker.enabled = on && SeatBlockers && !LetThrough(blocker);
        }

        private static void Squeeze(Vector3 at)
        {
            if (MakeWay(at)) Squeezes++;
        }

        // The obstacles round a point stand aside for a moment (a walker held up there, or a character about to
        // hop out of a thing: an agent switched on inside an obstacle would be shoved out sideways).
        public static bool MakeWay(Vector3 at)
        {
            bool any = false;
            blockers.RemoveAll(b => b == null);
            foreach (var b in blockers)
            {
                if (!b.enabled || Vector3.ProjectOnPlane(b.transform.position - at, Vector3.up).magnitude > SqueezeReach + b.radius * b.transform.lossyScale.x) continue;
                letThroughUntil[b] = Time.time + SqueezeTime;
                b.enabled = false;
                any = true;
            }
            return any;
        }

        // ------------------------------------------------------------ F51: on the person's palm
        // Let go over an open palm that faces up (or caught by one while falling), a character stands on it
        // and rides the hand: kinematic, a trigger, placed every frame by Windups.Handoff.PalmPerch. It counts
        // as held. When the palm tips over or is lost it falls as when dropped; a hand can pick it off.
        public bool OnPalm { get; private set; }
        public int PalmCount { get; private set; }
        // Asked the moment a hand lets go: is there a palm under it to stand on?
        public static System.Func<CapsulePetController, bool> PalmUnder;

        public bool StandOnPalm()
        {
            if (OnPalm) return true;
            if (Grabbed || HoldForIntro || Presented || !spawned || State == MotionState.Climbing || State == MotionState.WaitingForRoom) return false;
            if (MeetingSeatLocked) { MeetingSeatLocked = false; ReseatAfterDrop = true; }
            GrabCount++;
            EnterPalm();
            return true;
        }

        private void EnterPalm()
        {
            DisableAgent();
            SetBlocker(false);
            traversal = null;
            Phase = TraversalPhase.None;
            continueAfterTraversal = false;
            Hopping = false;
            if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            body.isKinematic = true;
            body.useGravity = false;
            capsule.isTrigger = true;
            OnPalm = true;
            wasHeld = true;
            PalmCount++;
            SetState(MotionState.Held);
        }

        public void RideOnPalm(Vector3 feet, Quaternion rotation)
        {
            if (!OnPalm) return;
            transform.SetPositionAndRotation(feet, rotation);
            body.position = feet;
            body.rotation = rotation;
        }

        public void LeavePalm(Vector3 velocity)
        {
            if (!OnPalm) return;
            OnPalm = false;
            capsule.isTrigger = false;
            if (Grabbed) return; // a hand has it now
            wasHeld = false;
            if (environment.IsReady) BeginFalling(velocity);
            else { body.isKinematic = true; body.useGravity = false; SetState(MotionState.WaitingForRoom); }
        }

        // ------------------------------------------------------------ F51: a hop the session asks for
        // Out of (or off the top of) something it must not stand in, onto a free point on a walkable surface:
        // the same crouch, arc and landing as a jump up furniture. False when it cannot hop now.
        public int HopCount { get; private set; }
        private CapsuleTraversalLink hopLink;
        public bool HopTo(Vector3 feet, float apex = 0.05f)
        {
            if (IsHeld || HoldForIntro || Presented || MeetingSeatLocked || !spawned || !environment.IsReady) return false;
            if (State != MotionState.Idle && State != MotionState.Walking && State != MotionState.Falling && State != MotionState.Recovering) return false;
            if (!environment.TrySameLevelNavPoint(feet, out var end)) return false;
            DisableAgent();
            if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            body.isKinematic = true;
            body.useGravity = false;
            var upright = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            transform.rotation = upright;
            body.rotation = upright;
            if (hopLink == null)
            {
                var holder = new GameObject("Hop (F51)");
                holder.transform.SetParent(environment.transform, false);
                hopLink = holder.AddComponent<CapsuleTraversalLink>();
                hopLink.Kind = CapsuleTraversalKind.Climb;
                hopLink.Jump = true;
                hopLink.Furniture = "hop";
            }
            hopLink.Apex = apex;
            hopLink.Waypoints = new[] { transform.position, end };
            traversal = hopLink;
            waypoint = 0;
            Phase = TraversalPhase.None;
            Hopping = false;
            continueAfterTraversal = false;
            Destination = end;
            HopCount++;
            SetState(MotionState.Climbing);
            return true;
        }
        public bool TryPlaceAt(Vector3 feet)
        {
            if(HoldForIntro || MeetingSeatLocked || IsHeld || !environment.IsReady || !environment.TrySameLevelNavPoint(feet,out var point)) return false;
            DisableAgent(); traversal=null;
            body.isKinematic=true;body.useGravity=false;
            transform.SetPositionAndRotation(point,Quaternion.identity);body.position=point;body.rotation=Quaternion.identity;
            spawned=true;SetVisible(true);return EnableAgent(point);
        }
        public void StopWalking()
        {
            if (State != MotionState.Walking || IsHeld || !agent.enabled || !agent.isOnNavMesh || agent.isOnOffMeshLink) return;
            agent.ResetPath();
            SetState(MotionState.Idle);
        }
        public CapsuleTraversalLink ActiveTraversal => traversal;

        // A jump up onto furniture (2026-11-11: hopping reads far better than floating up the side): the
        // character stands at the take-off point facing the top, crouches, flies a parabola that clears the
        // edge, and lands. Phase and progress are for the body animation.
        public enum TraversalPhase { None, Crouch, Air, Land }
        public TraversalPhase Phase { get; private set; }
        public float PhaseTime => Time.fixedTime - phaseStart;
        public bool Jumping => State == MotionState.Climbing && traversal != null && traversal.Jump;
        public int JumpCount { get; private set; }
        public int HopDownCount { get; private set; }
        public const float CrouchTime = .2f, LandTime = .22f;
        private float phaseStart, flightTime, launchUp;
        private Vector3 launchFrom, launchFlat;
        // A traversal on the way to a destination farther on: after the link (and a landing), keep going.
        private bool continueAfterTraversal;

        // Where the character is heading a short way ahead along its path (for the head to look there).
        private NavMeshPath aheadPath;
        private Vector3[] aheadCorners = new Vector3[16];
        private int aheadCount;
        private float aheadAt = -1f;
        public bool TryLookAhead(float distance, out Vector3 point)
        {
            point = transform.position;
            if (State == MotionState.Climbing && traversal != null)
            {
                point = traversal.Jump ? traversal.End
                    : traversal.Kind == CapsuleTraversalKind.Drop ? traversal.Waypoints[1] // over the edge it hops off
                    : traversal.Waypoints[Mathf.Clamp(waypoint, 0, traversal.Waypoints.Length - 1)];
                return true;
            }
            if (State != MotionState.Walking || !agent.enabled || !agent.isOnNavMesh || !agent.hasPath) return false;
            if (Time.time - aheadAt > .1f)
            {
                aheadAt = Time.time;
                aheadPath = agent.path;
                aheadCount = aheadPath.GetCornersNonAlloc(aheadCorners);
            }
            if (aheadCount < 2) { point = agent.steeringTarget; return true; }
            Vector3 from = transform.position;
            float left = distance;
            // Corners already passed are skipped: start from the corner nearest ahead.
            for (int i = 1; i < aheadCount; i++)
            {
                Vector3 to = aheadCorners[i];
                if (i == 1) from = transform.position;
                float d = Vector3.Distance(from, to);
                if (d >= left) { point = from + (to - from) * (left / Mathf.Max(d, 1e-5f)); return true; }
                left -= d;
                from = to;
            }
            point = aheadCorners[aheadCount - 1];
            return true;
        }

        private Rigidbody body;
        private NavMeshAgent agent;
        private Grabbable grabbable;
        private CapsuleCollider capsule;
        private Renderer[] renderers;
        private CapsuleTraversalLink traversal;
        private int waypoint;
        private float idleUntil;
        private float groundedSince = -1;
        private float lastProgress;
        private Vector3 lastProgressPosition;
        private bool spawned;
        private bool wasHeld;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            // Rigidbody interpolation restores its cached pose over ISDK's direct Transform updates.
            body.interpolation = RigidbodyInterpolation.None;
            agent = GetComponent<NavMeshAgent>();
            grabbable = GetComponent<Grabbable>();
            capsule = GetComponent<CapsuleCollider>();
            renderers = GetComponentsInChildren<Renderer>();
            agent.enabled = false;
            body.isKinematic = true;
            body.useGravity = false;
            SetVisible(false);
            grabbable.InjectOptionalThrowWhenUnselected(false);
            // This state machine owns Rigidbody mode; ISDK owns only the held pose.
            grabbable.InjectOptionalKinematicWhileSelected(false);
            grabbable.WhenPointerEventRaised += OnPointerEvent;
            // F51: what the others walk round while this one sits (off until it sits).
            blocker = AddBlocker(gameObject);
            float scale = Mathf.Max(0.0001f, transform.lossyScale.x);
            blocker.shape = NavMeshObstacleShape.Capsule;
            blocker.radius = BlockerRadius / scale;
            blocker.height = CapsuleRoomEnvironment.Height / scale;
            blocker.center = Vector3.up * (CapsuleRoomEnvironment.Height * 0.5f / scale);
        }

        private void Start()
        {
            environment.NavigationChanging += NavigationChanging;
            environment.NavigationReady += NavigationReady;
            if (environment.IsReady) NavigationReady();
            UpdateLabel();
        }
        private void OnDestroy()
        {
            if (grabbable != null) grabbable.WhenPointerEventRaised -= OnPointerEvent;
            if (environment != null)
            {
                environment.NavigationChanging -= NavigationChanging;
                environment.NavigationReady -= NavigationReady;
            }
        }
        private void OnPointerEvent(PointerEvent _) => SynchronizeGrab();
        private void SynchronizeGrab()
        {
            if (OnPalm && Grabbed)
            {
                // Picked off the palm: an ordinary hold from here.
                OnPalm = false;
                capsule.isTrigger = false;
            }
            if (MeetingSeatLocked)
            {
                if (!IsHeld) return;
                // Grabbed off its seat: carried and dropped like any time (it falls onto the room's surfaces).
                MeetingSeatLocked = false;
                capsule.isTrigger = false;
                SetBlocker(false);
                ReseatAfterDrop = true;
                wasHeld = false;
            }
            bool held = IsHeld;
            if (held == wasHeld) return;
            wasHeld = held;
            if (held)
            {
                Presented = false;
                GrabCount++;
                DisableAgent();
                traversal = null;
                Phase = TraversalPhase.None;
                continueAfterTraversal = false;
                Hopping = false;
                body.isKinematic = true;
                body.useGravity = false;
                SetState(MotionState.Held);
            }
            // F51: let go over an open palm, it stands there instead of falling through the hand.
            else if (spawned && PalmUnder != null && PalmUnder(this)) EnterPalm();
            else if (environment.IsReady) BeginFalling(Vector3.zero);
            else { body.isKinematic = true; body.useGravity = false; SetState(MotionState.WaitingForRoom); }
        }

        // Launch intro (F28): a character can be kept out of the room (hidden, not spawned) until the
        // title presents it at a point (kinematic, visible), then launches it; from there the usual
        // falling, landing and walking take over.
        public bool HoldForIntro { get; set; }
        public bool Presented { get; private set; }

        public void Present(Vector3 feet, Quaternion rotation, bool visible = true)
        {
            if (IsHeld) return;
            DisableAgent();
            traversal = null;
            body.isKinematic = true;
            body.useGravity = false;
            transform.SetPositionAndRotation(feet, rotation);
            body.position = feet;
            body.rotation = rotation;
            SetVisible(visible);
            spawned = true;
            Presented = true;
            SetState(MotionState.WaitingForRoom);
        }

        // F35: the end of a hop the intro drives itself (onto a seat): it stands right there, facing the way it
        // faced, and walks on from there; if that is no walkable point, it falls from there as a launch would.
        public bool SettleAt(Vector3 feet)
        {
            HoldForIntro = false;
            if (IsHeld) return false;
            Presented = false;
            if (MeetingSeatLocked || !environment.IsReady || !environment.TrySameLevelNavPoint(feet, out var point))
            {
                if (environment.IsReady) BeginFalling(Vector3.zero); else ResetToFloor();
                return false;
            }
            DisableAgent();
            traversal = null;
            body.isKinematic = true;
            body.useGravity = false;
            var turn = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            transform.SetPositionAndRotation(point, turn);
            body.position = point;
            body.rotation = turn;
            spawned = true;
            SetVisible(true);
            return EnableAgent(point);
        }

        public void Launch(Vector3 velocity)
        {
            HoldForIntro = false;
            if (!Presented || IsHeld) return;
            Presented = false;
            if (environment.IsReady) BeginFalling(velocity);
            else ResetToFloor();
        }

        private void NavigationChanging()
        {
            if (MeetingSeatLocked || IsHeld || Presented) return;
            DisableAgent();
            traversal = null;
            body.isKinematic = true;
            body.useGravity = false;
            SetState(MotionState.WaitingForRoom);
        }
        private void NavigationReady()
        {
            if (MeetingSeatLocked || IsHeld || Presented) return;
            if (!spawned) { if (!HoldForIntro) ResetToFloor(); return; }
            if (!TryResumeWalking()) BeginFalling(Vector3.zero);
        }

        public void ResetToFloor()
        {
            if (MeetingSeatLocked || IsHeld || !environment.IsReady || !environment.TryRandomPoint(true, out var point)) return;
            DisableAgent();
            traversal = null;
            body.isKinematic = true;
            body.useGravity = false;
            transform.SetPositionAndRotation(point, Quaternion.identity);
            body.position = point;
            body.rotation = Quaternion.identity;
            SetVisible(true);
            spawned = true;
            EnableAgent(point);
        }

        private void SetVisible(bool visible)
        {
            foreach (var renderer in renderers) renderer.enabled = visible;
            if (capsule != null) capsule.enabled = visible;
        }
        private void DisableAgent()
        {
            if (!agent.enabled) return;
            if (agent.isOnNavMesh && !agent.isOnOffMeshLink) agent.ResetPath();
            agent.enabled = false;
        }
        private bool EnableAgent(Vector3 point)
        {
            SetBlocker(false); // never an agent and its own obstacle at once
            body.isKinematic = true;
            body.useGravity = false;
            transform.position = point;
            body.position = point;
            agent.agentTypeID = environment.AgentTypeId;
            agent.speed = walkingSpeed;
            agent.angularSpeed = 180;
            agent.acceleration = 0.8f;
            agent.radius = CapsuleRoomEnvironment.Radius;
            agent.height = CapsuleRoomEnvironment.Height;
            agent.baseOffset = 0;
            agent.stoppingDistance = 0.025f;
            agent.autoTraverseOffMeshLink = false;
            agent.updatePosition = true;
            agent.updateRotation = true;
            agent.enabled = true;
            if (!agent.Warp(point)) { agent.enabled = false; SetState(MotionState.Recovering); return false; }
            SetState(MotionState.Idle);
            idleUntil = Time.time + Random.Range(1f, 3f);
            return true;
        }

        private void Update()
        {
            SynchronizeGrab();
            // F51: the seat obstacle follows the seat (and comes back after a walker was let through).
            if (blocker != null)
            {
                bool block = blockerWanted && MeetingSeatLocked && SeatBlockers && !LetThrough(blocker);
                if (blocker.enabled != block) blocker.enabled = block;
            }
            if (MeetingSeatLocked) return;
            if (!environment.IsReady || IsHeld || !spawned) return;
            if (ReseatAfterDrop && State == MotionState.Idle)
            {
                ReseatAfterDrop = false;
                LockMeetingSeat();
                return;
            }
            if (RoamingEnabled && !InteractionPaused && State == MotionState.Idle && Time.time >= idleUntil) ChooseDestination();
            if (State != MotionState.Walking) return;
            if (!agent.enabled || !agent.isOnNavMesh) { BeginFalling(Vector3.zero); return; }
            if (agent.isOnOffMeshLink)
            {
                traversal = environment.FindLink(agent.currentOffMeshLinkData);
                if (traversal == null) { BeginFalling(Vector3.zero); return; }
                agent.updatePosition = false;
                agent.updateRotation = false;
                waypoint = 0;
                Phase = TraversalPhase.None;
                LastTraversal = traversal;
                continueAfterTraversal = Vector3.Distance(Destination, traversal.End) > 0.06f;
                SetState(MotionState.Climbing);
                return;
            }
            if (agent.pathPending) return;
            if (agent.pathStatus != NavMeshPathStatus.PathComplete || agent.remainingDistance <= arriveWithin)
            {
                agent.ResetPath();
                SetState(MotionState.Idle);
                idleUntil = Time.time + Random.Range(1f, 3f);
                return;
            }
            if (Vector3.Distance(lastProgressPosition, transform.position) > 0.015f)
            {
                lastProgressPosition = transform.position;
                lastProgress = Time.time;
                squeezedAt = -1f;
            }
            else if (Time.time - lastProgress > 8f)
            {
                agent.ResetPath(); SetState(MotionState.Idle); idleUntil = Time.time + 1;
            }
            // F51: held up by something it walks round (a seated friend, a made thing) with no way past: let through.
            else if (Time.time - lastProgress > StuckBeforeSqueeze && squeezedAt < 0f)
            {
                squeezedAt = Time.time;
                Squeeze(transform.position);
            }
        }

        private void ChooseDestination()
        {
            // Give surface transitions equal opportunity instead of letting the larger floor dominate every choice.
            var options = new List<CapsuleTraversalLink>();
            foreach (var link in environment.Links)
                if (Mathf.Abs(link.Start.y - transform.position.y) < 0.05f) options.Add(link);
            if (options.Count > 0 && Random.value < 0.65f)
            {
                int offset = Random.Range(0, options.Count);
                for (int i = 0; i < options.Count; i++) if (GoTo(options[(offset + i) % options.Count].End)) return;
            }
            for (int attempt = 0; attempt < 16; attempt++)
                if (environment.TryRandomPoint(false, out var target) && Vector3.Distance(target, transform.position) > 0.15f && GoTo(target)) return;
            idleUntil = Time.time + 2f;
        }

        // `precise`: arrive within PreciseArrive instead of the usual few centimetres (a character that has to
        // stand at an exact spot, e.g. beside its work: F33). Ordinary walks stay as they were.
        public const float UsualArrive = 0.035f, PreciseArrive = 0.012f;
        private float arriveWithin = UsualArrive;
        private bool walkPrecise;
        private float squeezedAt = -1f;
        public bool GoTo(Vector3 destination, float maximumPathLength, bool precise)
        {
            if (!GoTo(destination, maximumPathLength)) return false;
            walkPrecise = precise;
            if (precise) { agent.stoppingDistance = 0.004f; arriveWithin = PreciseArrive; }
            return true;
        }

        public bool GoTo(Vector3 destination, float maximumPathLength = float.PositiveInfinity)
        {
            if (MeetingSeatLocked) return false;
            if (agent.enabled) agent.stoppingDistance = 0.025f;
            arriveWithin = UsualArrive;
            walkPrecise = false;
            if (IsHeld || !environment.IsReady || !agent.enabled || !agent.isOnNavMesh ||
                (State != MotionState.Idle && State != MotionState.Walking)) return false;
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(transform.position, destination, environment.Query, path) || path.status != NavMeshPathStatus.PathComplete) return false;
            if (!float.IsPositiveInfinity(maximumPathLength))
            {
                float length = 0f;
                var corners = path.corners;
                for (int i = 1; i < corners.Length; i++)
                    length += Vector3.Distance(corners[i - 1], corners[i]);
                if (length > maximumPathLength) return false;
            }
            if (!agent.SetPath(path)) return false;
            continueAfterTraversal = false;
            Destination = destination;
            lastProgress = Time.time;
            lastProgressPosition = transform.position;
            squeezedAt = -1f;
            SetState(MotionState.Walking);
            return true;
        }

        private void FixedUpdate()
        {
            // ISDK can select in Update or between fixed steps; never write over its held pose.
            if (IsHeld || !environment.IsReady || !spawned) return;
            if (State == MotionState.Climbing) AdvanceTraversal();
            else if (State == MotionState.Falling || State == MotionState.Recovering) CheckLanding();
        }
        // The route a traversal last used (the walk-back avoids it for a while if it failed there).
        public CapsuleTraversalLink LastTraversal { get; private set; }
        public int TraversalFailures { get; private set; }

        private void AdvanceTraversal()
        {
            if (traversal == null) { BeginFalling(Vector3.zero); return; }
            if (traversal.Jump) { AdvanceJump(); return; }
            if (traversal.Kind == CapsuleTraversalKind.Drop) { AdvanceHopDown(); return; }
            if (waypoint >= traversal.Waypoints.Length) { CompleteClimb(); return; }
            var next = Vector3.MoveTowards(body.position, traversal.Waypoints[waypoint], climbingSpeed * Time.fixedDeltaTime);
            if (!environment.PathIsClear(new[] { body.position, next })) { TraversalFailures++; traversal = null; BeginFalling(Vector3.zero); return; }
            body.MovePosition(next);
            if (Vector3.Distance(next, traversal.Waypoints[waypoint]) > 0.003f) return;
            waypoint++;
        }

        // Up in one jump: face the top, crouch, fly the arc the room checked when it made the route, land.
        private void AdvanceJump()
        {
            float now = Time.fixedTime;
            switch (Phase)
            {
                case TraversalPhase.None:
                {
                    launchFrom = body.position;
                    var flat = Vector3.ProjectOnPlane(traversal.End - launchFrom, Vector3.up);
                    if (flat.sqrMagnitude > 1e-6f)
                    {
                        var face = Quaternion.LookRotation(flat.normalized, Vector3.up);
                        body.MoveRotation(face);
                        transform.rotation = face;
                    }
                    Phase = TraversalPhase.Crouch;
                    phaseStart = now;
                    return;
                }
                case TraversalPhase.Crouch:
                {
                    if (now - phaseStart < CrouchTime) return;
                    var arc = new Vector3[10];
                    for (int i = 0; i < arc.Length; i++) arc[i] = CapsuleRoomEnvironment.JumpPoint(launchFrom, traversal.End, traversal.Apex, i / (arc.Length - 1f));
                    // Something stands in the way now (it was clear when the route was made): stay down.
                    if (!environment.PathIsClear(arc)) { TraversalFailures++; AbortTraversal(); return; }
                    float rise = traversal.End.y - launchFrom.y, apex = Mathf.Max(rise, 0f) + traversal.Apex;
                    launchUp = Mathf.Sqrt(2f * CapsuleRoomEnvironment.JumpGravity * apex);
                    flightTime = CapsuleRoomEnvironment.JumpTime(launchFrom, traversal.End, traversal.Apex);
                    launchFlat = Vector3.ProjectOnPlane(traversal.End - launchFrom, Vector3.up) / flightTime;
                    Phase = TraversalPhase.Air;
                    phaseStart = now;
                    JumpCount++;
                    return;
                }
                case TraversalPhase.Air:
                {
                    float t = now - phaseStart;
                    if (t >= flightTime)
                    {
                        body.MovePosition(traversal.End);
                        Phase = TraversalPhase.Land;
                        phaseStart = now;
                        return;
                    }
                    float g = CapsuleRoomEnvironment.JumpGravity;
                    body.MovePosition(launchFrom + launchFlat * t + Vector3.up * (launchUp * t - 0.5f * g * t * t));
                    return;
                }
                case TraversalPhase.Land:
                    if (now - phaseStart < LandTime) return;
                    Phase = TraversalPhase.None;
                    CompleteClimb();
                    return;
            }
        }

        // Down: walk to just before the edge, crouch, and hop off a little way out (physics does the fall).
        public const float HopOut = 0.3f, HopUp = 0.5f;
        private void AdvanceHopDown()
        {
            Vector3 top = traversal.Waypoints[0], beyond = traversal.Waypoints[1];
            // The edge is about halfway from the top point to the point beyond it; hop from a little before it.
            Vector3 hop = Vector3.Lerp(top, new Vector3(beyond.x, top.y, beyond.z), 0.42f);
            Vector3 flat = Vector3.ProjectOnPlane(beyond - top, Vector3.up);
            if (Phase == TraversalPhase.None)
            {
                var next = Vector3.MoveTowards(body.position, hop, walkingSpeed * Time.fixedDeltaTime);
                if (flat.sqrMagnitude > 1e-6f) { var face = Quaternion.LookRotation(flat.normalized, Vector3.up); body.MoveRotation(face); transform.rotation = face; }
                body.MovePosition(next);
                if (Vector3.Distance(next, hop) > 0.003f) return;
                Phase = TraversalPhase.Crouch;
                phaseStart = Time.fixedTime;
                return;
            }
            if (Time.fixedTime - phaseStart < CrouchTime) return;
            DropCount++;
            HopDownCount++;
            traversal = null;
            Phase = TraversalPhase.None;
            body.position = hop;
            BeginFalling((flat.sqrMagnitude > 1e-6f ? flat.normalized : transform.forward) * HopOut + Vector3.up * HopUp);
            Hopping = true;
        }

        // In the air after hopping off a ledge on purpose (not dropped or fallen): the body jumps, not flails.
        public bool Hopping { get; private set; }

        // Gives up the traversal where it stands (still at the take-off point) and walks on from there.
        private void AbortTraversal()
        {
            traversal = null;
            Phase = TraversalPhase.None;
            continueAfterTraversal = false;
            DisableAgent();
            if (!TryResumeWalking()) BeginFalling(Vector3.zero);
        }

        private void CompleteClimb()
        {
            var end = traversal.End;
            ClimbCount++;
            traversal = null;
            Phase = TraversalPhase.None;
            body.position = end;
            transform.position = end;
            if (agent.enabled && agent.isOnOffMeshLink) agent.CompleteOffMeshLink();
            DisableAgent();
            if (EnableAgent(end)) ContinueToDestination();
        }

        // After a route (and the landing from a hop down), keep walking to where it was going.
        private void ContinueToDestination()
        {
            if (!continueAfterTraversal) return;
            continueAfterTraversal = false;
            bool precise = walkPrecise;
            if (Vector3.Distance(transform.position, Destination) > 0.05f) GoTo(Destination, float.PositiveInfinity, precise);
        }

        private void BeginFalling(Vector3 velocity)
        {
            DisableAgent();
            traversal = null;
            Phase = TraversalPhase.None;
            Hopping = false;
            // Preserve the actual capsule center while returning a released, rotated capsule upright.
            var center = transform.TransformPoint(capsule.center);
            var upright = Quaternion.Euler(0, transform.eulerAngles.y, 0);
            transform.SetPositionAndRotation(center - Vector3.up * CapsuleRoomEnvironment.Height * 0.5f, upright);
            body.position = transform.position;
            body.rotation = upright;
            body.isKinematic = false;
            body.useGravity = true;
            body.linearVelocity = velocity;
            body.angularVelocity = Vector3.zero;
            groundedSince = -1;
            SetState(MotionState.Falling);
        }
        private void CheckLanding()
        {
            if (environment.TrySupport(transform.position, 0.06f, out var support, out var anchor) &&
                anchor.HasAnyLabel(CapsuleRoomEnvironment.WalkableLabels) &&
                Mathf.Abs(support.point.y - transform.position.y) < 0.035f && body.linearVelocity.magnitude < 0.12f)
            {
                if (groundedSince < 0) { groundedSince = Time.time; SetState(MotionState.Recovering); }
                if (Time.time - groundedSince >= 0.5f && TryResumeWalking()) { LandingCount++; ContinueToDestination(); }
            }
            else { groundedSince = -1; SetState(MotionState.Falling); }
            var bounds = environment.Room.GetRoomBounds();
            if (transform.position.y < bounds.min.y - 2f)
            {
                RecoveryCount++;
                ResetToFloor();
            }
        }
        private bool TryResumeWalking()
        {
            if (!environment.TrySupport(transform.position, 0.08f, out var support, out var anchor) ||
                !anchor.HasAnyLabel(CapsuleRoomEnvironment.WalkableLabels) ||
                Mathf.Abs(support.point.y - transform.position.y) > 0.04f ||
                !environment.TrySameLevelNavPoint(support.point, out var point)) return false;
            body.isKinematic = true;
            body.useGravity = false;
            Hopping = false;
            return EnableAgent(point);
        }
        private void SetState(MotionState state)
        {
            if (State == state) return;
            State = state;
            UpdateLabel();
        }
        private void UpdateLabel()
        {
            if (stateLabel == null) return;
            string action = State == MotionState.Climbing && traversal != null && traversal.Kind == CapsuleTraversalKind.Drop ? "Hopping off ledge"
                : State == MotionState.Climbing && traversal != null && traversal.Jump ? "Jumping up" : State.ToString();
            stateLabel.text = "CAPSULE: " + action + " | Grab to hold / Release to drop";
        }
    }
}
