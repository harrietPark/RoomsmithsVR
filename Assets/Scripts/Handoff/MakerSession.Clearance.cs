using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Pet = RoomCapsule.CapsulePetController;

namespace Windups.Handoff
{
    // F51 (2026-11-12, after a Quest try: "the characters still overlap each other, and the post-its and the things
    // made all overlap in between"). Every part of the session kept its own list of what is in the way, each
    // missing something, and nothing looked again after the person moved a character or a made thing. Now:
    //   * Footprints() is the one map of what stands on the surfaces: the crew, the things made and v2's twin,
    //     idea notes, the ring and its note, the mic, the notebook, the drawing page, the board on a desk,
    //     standing flags, the show-me circle and the real things Spark placed.
    //   * FreeStand() finds where a teammate can stand clear of all of it (and of the others, and of Maker's
    //     work space); votes, walk-backs, tours, call-overs and stepping aside use it.
    //   * ClearanceFrame() looks four times a second. A teammate that has come to rest overlapping a friend or
    //     a thing (put down there by the person, a thing dropped onto it, a seat that filled up) walks to the
    //     nearest free spot, or hops out when it stands inside or on top of something; an idea note under a
    //     thing slides out. Things the person placed never move. Each overlap is written to maker_input_log.txt.
    //   * The crew walks round things made (navigation obstacles that do not cut the mesh; see
    //     CapsulePetController.SeatBlockers for the seated crew).
    public sealed partial class MakerSession
    {
        public enum FootKind { Crew, Made, IdeaNote, RingNote, Ring, Mic, Notebook, Page, Board, Flag, Circle, Real }

        public struct Foot
        {
            public FootKind kind;
            public string name;
            public Transform owner;
            public Crew crew;       // kind == Crew
            public Vector3 at;      // its middle, at the height of its base
            public float x, z;      // half extents along world x and z (round: both the radius)
            public bool round;
            public float top;       // how high it reaches above its base
        }

        public const float CrewBody = .05f;         // how far a teammate's body and mittens reach sideways
        public const float ThingClear = .012f;      // a teammate stands at least this clear of a thing
        public const float StandApart = .18f;       // friends, middle to middle, when the roomy 22 cm does not fit
        public const float CrewSlack = .004f;       // under the crew's minimum spacing by more than this: one of the two moves
        public const float NoteFoot = .045f;        // an idea note with its sticker, as a circle on the desk
        public const float SettledFor = .6f;        // only what has stood still this long is judged
        public static bool KeepClear = true;

        public int ClearMoves { get; private set; }     // walks to a free spot
        public int ClearHops { get; private set; }      // hops out of / off something
        public int NoteSlides { get; private set; }
        public int WalkThroughs { get; private set; }   // a walker's body passed through a friend standing still
        public readonly List<string> OverlapLog = new List<string>();

        private readonly Dictionary<Crew, (Vector3 at, float since)> stoodAt = new Dictionary<Crew, (Vector3, float)>();
        private readonly Dictionary<Crew, float> restingSince = new Dictionary<Crew, float>();
        private readonly Dictionary<Crew, Vector3> headingTo = new Dictionary<Crew, Vector3>();
        private readonly Dictionary<string, float> overlapNoted = new Dictionary<string, float>();
        private readonly HashSet<string> insideNow = new HashSet<string>();
        private readonly Dictionary<Transform, NavMeshObstacle> thingBlockers = new Dictionary<Transform, NavMeshObstacle>();
        private readonly Dictionary<Crew, float> nextClearTry = new Dictionary<Crew, float>();
        private float nextClearance, makerBusyAt = -100f;
        public const float MakerAfterWork = 4f;
        // Kept between looks (this runs four times a second for the whole session: no lists, arrays or strings
        // made each time).
        private static readonly List<Renderer> rendererBuffer = new List<Renderer>();
        private readonly List<Foot> footBuffer = new List<Foot>();
        private readonly List<RoomEyes.Found> realBuffer = new List<RoomEyes.Found>();
        private readonly HashSet<Crew> settledNow = new HashSet<Crew>();
        private readonly HashSet<string> walkNow = new HashSet<string>();
        private static readonly Dictionary<Crew, string> noteNames = new Dictionary<Crew, string>(), flagNames = new Dictionary<Crew, string>();
        private readonly Dictionary<RoomEyes.Found, string> realNames = new Dictionary<RoomEyes.Found, string>();

        private static string NoteName(Crew who)
        {
            if (!noteNames.TryGetValue(who, out var name)) noteNames[who] = name = "idea note (" + MakerRoles.Name(who) + ")";
            return name;
        }

        private static string FlagName(Crew? who)
        {
            if (!who.HasValue) return "flag (yours)";
            if (!flagNames.TryGetValue(who.Value, out var name)) flagNames[who.Value] = name = "flag (" + MakerRoles.Name(who.Value) + ")";
            return name;
        }

        private string RealName(RoomEyes.Found found)
        {
            if (!realNames.TryGetValue(found, out var name))
            {
                if (realNames.Count > 64) realNames.Clear();
                realNames[found] = name = found.seen != null ? MakerRoles.Clean(found.seen.name) : "thing";
            }
            return name;
        }

        // ------------------------------------------------------------ the map

        private static bool BoundsOf(Transform root, bool firstMeshOnly, out Bounds bounds)
        {
            bounds = default;
            if (root == null || !root.gameObject.activeInHierarchy) return false;
            if (firstMeshOnly)
            {
                var first = root.GetComponentInChildren<MeshRenderer>();
                if (first == null) return false;
                bounds = first.bounds;
                return true;
            }
            bool any = false;
            root.GetComponentsInChildren(rendererBuffer);
            foreach (var r in rendererBuffer)
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            rendererBuffer.Clear();
            return any;
        }

        private static Foot FootOf(FootKind kind, string name, Transform owner, Bounds b, float grow = 0f) => new Foot
        {
            kind = kind, name = name, owner = owner, at = new Vector3(b.center.x, b.min.y, b.center.z),
            x = b.extents.x + grow, z = b.extents.z + grow, round = false, top = b.size.y
        };

        private static Foot Round(FootKind kind, string name, Transform owner, Vector3 at, float radius, float top) =>
            new Foot { kind = kind, name = name, owner = owner, at = at, x = radius, z = radius, round = true, top = top };

        // Everything that stands on the surfaces right now (not what is in a hand or in the air on a palm).
        public List<Foot> Footprints(bool withCrew = true) => FootprintsInto(new List<Foot>(), withCrew);

        private List<Foot> FootprintsInto(List<Foot> feet, bool withCrew)
        {
            feet.Clear();
            if (withCrew)
                foreach (var pair in cast)
                {
                    var c = pair.Value;
                    if (c == null || !c.gameObject.activeInHierarchy || c.movement == null || c.movement.IsHeld) continue;
                    var f = Round(FootKind.Crew, MakerRoles.Name(pair.Key), c.transform, c.transform.position, CrewBody, RoomCapsule.CapsuleRoomEnvironment.Height);
                    f.crew = pair.Key;
                    feet.Add(f);
                }
            foreach (var t in MadeThings())
            {
                if (t == placementView || t == held) continue;
                // (F52) What stands on a stand that is in the hand goes with it.
                if (held != null && IsStand(held))
                {
                    bool riding = false;
                    foreach (var r in riders) if (r.thing == t) { riding = true; break; }
                    if (riding) continue;
                }
                if (IsStand(t))
                {
                    // A display stand: a low round dish.
                    feet.Add(Round(FootKind.Made, "stand", t, t.position, StandRadiusOf(t), StandHeight));
                    continue;
                }
                if (BoundsOf(t, true, out var b)) feet.Add(FootOf(FootKind.Made, madeNames.TryGetValue(t, out var n) && !string.IsNullOrEmpty(n) ? n : t.name, t, b));
            }
            if (draftMesh != null && Draft != null && Draft != held)
            {
                bool listed = false;
                foreach (var f in feet) if (f.owner == Draft) { listed = true; break; }
                if (!listed && BoundsOf(draftMesh, true, out var twin)) feet.Add(FootOf(FootKind.Made, "v2 twin", Draft, twin));
            }
            foreach (var idea in Ideas)
                if (idea != null && idea.root != null && idea.placed && BoundsOf(idea.root, false, out var nb))
                    feet.Add(FootOf(FootKind.IdeaNote, NoteName(idea.who), idea.root, nb));
            if (arrival.TopicRing != null && arrival.TopicRing.enabled)
                feet.Add(Round(FootKind.Ring, "ring", arrival.TopicRing.transform, arrival.TopicRing.transform.position, arrival.RingRadius, .01f));
            if (ringNote != null && !NoteOnWall && ringNote.gameObject.activeInHierarchy && ringNote != carriedNote && BoundsOf(ringNote, false, out var rn))
                feet.Add(FootOf(FootKind.RingNote, "ring note", ringNote, rn));
            if (arrival.MicShown && !arrival.MicHeld)
                feet.Add(Round(FootKind.Mic, "mic", null, arrival.MicRest - Vector3.up * WindupsArrival.MicBase, .04f, .1f));
            if (notebookClosed != null && BoundsOf(notebookClosed, false, out var book)) feet.Add(FootOf(FootKind.Notebook, "notebook", notebookClosed, book));
            if (notebookView != null && !NotebookHeld && BoundsOf(notebookView, false, out var open)) feet.Add(FootOf(FootKind.Notebook, "open notebook", notebookView, open));
            // F94 (Quest 2026-11-15: in Change shape Maker kept holding its book out and turning away, every 1.5 s for 45 s):
            // the book in Maker's hands (shown as its tool, or offered in the shape step) is not a page on the desk; it
            // was, so Maker "overlapped" its own book and walked clear of it, the book going along.
            if (paper != null && !SketchbookHeld && !takingSketchbook && !paperInFlight && !toolBook && !OfferingBook && BoundsOf(paper, false, out var page))
                feet.Add(FootOf(FootKind.Page, "drawing page", paper, page));
            if (board != null && Wall.place == "surface" && BoundsOf(board, false, out var bb)) feet.Add(FootOf(FootKind.Board, "idea board", board, bb));
            foreach (var flag in Flags)
            {
                if (flag == null || flag.root == null || !flag.root.gameObject.activeInHierarchy) continue;
                if (flag.place != FlagPlace.Offered && !(flag.place == FlagPlace.Parked && flag.stand != null && flag.stand.gameObject.activeSelf)) continue;
                var f = Round(FootKind.Flag, FlagName(flag.who), flag.root, flag.root.position, .012f, .06f);
                if (flag.who.HasValue) f.crew = flag.who.Value;
                feet.Add(f);
            }
            // F56: Spark's frame while it lies on a surface (in the air it takes no place), and Fixer's basket.
            if (ShowCircleShown && FrameLying && !FrameBusy)
                feet.Add(Round(FootKind.Circle, "show-me frame", showFrame, ShowCircleCentre, ShowFootRadius, .02f));
            if (TrashShown) feet.Add(Round(FootKind.Circle, "basket", trashBasket, trashBasket.position, .04f, .05f));
            // Real things Spark placed from the camera and depth (the room model knows nothing of a mug). New spots
            // keep clear of them; nobody is sent away for one (where and how big they are is an estimate, and in
            // passthrough a character on a real thing does not read as two things in one place).
            var real = realBuffer;
            real.Clear();
            if (Target != null && Target.onSurface) real.Add(Target);
            if (Eyes != null) foreach (var f in Eyes.LastFound) if (f != null && f.onSurface && f.placed && !real.Contains(f)) real.Add(f);
            foreach (var f in real)
                feet.Add(Round(FootKind.Real, RealName(f), null, f.basePoint, Mathf.Clamp(f.width * .5f, .02f, .12f), Mathf.Max(.02f, f.height)));
            AddHandRoom(feet); // F101: the hands at work (new spots keep clear of them)
            return feet;
        }

        // How far apart two footprints are seen from above (negative: how deep they overlap); "far" when one is
        // wholly above the other.
        public static float FootGap(in Foot a, in Foot b)
        {
            if (a.at.y + a.top < b.at.y - .008f || b.at.y + b.top < a.at.y - .008f) return float.MaxValue;
            float dx = Mathf.Abs(a.at.x - b.at.x), dz = Mathf.Abs(a.at.z - b.at.z);
            if (a.round && b.round) return Mathf.Sqrt(dx * dx + dz * dz) - a.x - b.x;
            if (!a.round && !b.round)
            {
                float gx = dx - a.x - b.x, gz = dz - a.z - b.z;
                return gx > 0f && gz > 0f ? Mathf.Sqrt(gx * gx + gz * gz) : Mathf.Max(gx, gz);
            }
            var circle = a.round ? a : b;
            var box = a.round ? b : a;
            float ox = dx - box.x, oz = dz - box.z;
            if (ox > 0f || oz > 0f) return Mathf.Sqrt(Mathf.Max(ox, 0f) * Mathf.Max(ox, 0f) + Mathf.Max(oz, 0f) * Mathf.Max(oz, 0f)) - circle.x;
            return Mathf.Max(ox, oz) - circle.x;
        }

        // What this teammate may stand right next to: Maker and its own work, a flag in front of the one who
        // offers it, the notebook that follows Maker.
        private bool Belongs(Crew who, in Foot thing)
        {
            if (thing.kind == FootKind.Flag && thing.crew == who && thing.owner != null)
            {
                Transform root = thing.owner;
                if (Flags.Any(f => f.root == root && f.place == FlagPlace.Offered && f.who == who)) return true;
            }
            if (who == Crew.Maker && thing.kind == FootKind.Notebook) return true;
            if (who == Crew.Maker && MakerWorking && thing.kind == FootKind.Made) return true;
            return false;
        }

        private Foot BodyAt(Crew who, Vector3 feet)
        {
            var f = Round(FootKind.Crew, MakerRoles.Name(who), null, feet, CrewBody, RoomCapsule.CapsuleRoomEnvironment.Height);
            f.crew = who;
            return f;
        }

        // Is a teammate standing at `feet` clear of every thing on the map (not of the others)?
        public bool StandClear(Crew who, Vector3 feet, List<Foot> things = null)
        {
            things ??= Footprints(false);
            var body = BodyAt(who, feet);
            foreach (var t in things)
                if (t.kind != FootKind.Crew && !Belongs(who, t) && FootGap(body, t) < ThingClear) return false;
            return true;
        }

        // Where the others stand, or are walking to, or have just been sent.
        private List<Vector3> OthersStanding(Crew who)
        {
            var list = new List<Vector3>();
            foreach (var pair in cast)
            {
                if (pair.Key == who || pair.Value == null || pair.Value.movement == null || pair.Value.movement.IsHeld) continue;
                var pet = pair.Value.movement;
                list.Add(pet.transform.position);
                if (pet.State == Pet.MotionState.Walking || pet.State == Pet.MotionState.Climbing) list.Add(pet.Destination);
                if (headingTo.TryGetValue(pair.Key, out var sent)) list.Add(sent);
            }
            // Maker is away at its work: its seat is kept for it.
            if (who != Crew.Maker && Sculpt != SculptStage.None) list.Add(makerHome);
            return list;
        }

        // The free spot nearest `want` for this teammate: on the level of `want`, walkable, seen from the eyes,
        // clear of every thing, of Maker's work space, and of the others (roomy first, then the minimum).
        // `claimed`: spots promised to others in the same breath (voters, a call-over).
        // `othersStay` false: everyone is about to move (a call-over), so only the claimed spots count.
        public bool FreeStand(Crew who, Vector3 want, float reach, out Vector3 spot, IList<Vector3> claimed = null, bool needWalk = true, bool othersStay = true)
        {
            spot = want;
            var room = arrival.handoff.meeting.environment;
            if (room == null || !cast.TryGetValue(who, out var me) || me == null) return false;
            var things = Footprints(false);
            var others = othersStay ? OthersStanding(who) : new List<Vector3>();
            if (claimed != null) others.AddRange(claimed);
            var zones = who == Crew.Maker ? new List<(Vector3, float)>() : MakerWorkZones();
            Vector3 from = me.transform.position;
            Vector3 head = Head != null ? Head.position : want + Vector3.up * .5f;
            float least = GatherFormation.Settings.MinSpacing;
            // Roomy (the crew's personal space, also as seen from the eyes), then a hand's width, then the minimum.
            foreach (int tier in new[] { 0, 1, 2 })
            {
                bool roomy = tier == 0;
                float min = tier == 1 ? Mathf.Max(least, StandApart) : least;
                if (tier == 2 && min >= StandApart) break;
                // Never a long search in one frame, and each tier has its own share of the room's queries (a
                // crowded roomy tier used them all up, and "no free spot" was said with one a step away).
                int samples = 0, most = tier == 2 ? 90 : 45;
                // (A roomy spot only if it is near: stepping aside is a step, not a walk across the desk.)
                float tierReach = roomy ? Mathf.Min(reach, .28f) : reach;
                for (float r = 0f; r <= tierReach + .001f && samples <= most; r += .035f)
                {
                    int steps = r < .01f ? 1 : Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * r / .045f), 8, 28);
                    float best = float.MaxValue;
                    bool found = false;
                    for (int i = 0; i < steps; i++)
                    {
                        float a = i * 2f * Mathf.PI / steps;
                        Vector3 probe = want + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                        // The cheap tests first (plain arithmetic), the room's queries only for what passes.
                        bool ok = true;
                        foreach (var o in others)
                        {
                            if (Mathf.Abs(o.y - probe.y) > .2f) continue;
                            float d = Flat(o, probe);
                            if (d < min || (roomy && (d < GatherFormation.CrewSpacing || GatherFormation.AngleSeen(head, o, probe) < GatherFormation.CrewAngle))) { ok = false; break; }
                        }
                        if (!ok || !StandClear(who, probe, things) || IntoZones(zones, probe, CrewBodyRadius + .015f) > 0f) continue;
                        float cost = Flat(probe, want) + .2f * Vector3.Distance(probe, head);
                        if (cost >= best) continue;
                        if (++samples > most) break;
                        if (!room.TrySameLevelNavPoint(probe, out var nav) || Mathf.Abs(nav.y - want.y) > .04f || Flat(nav, probe) > .02f) continue;
                        if (Head != null && Physics.Linecast(head, nav + Vector3.up * .12f, room.GeometryMask, QueryTriggerInteraction.Ignore)) continue;
                        if (needWalk && !Walkable(from, nav, 3f) && Mathf.Abs(from.y - nav.y) < .06f) continue;
                        best = cost;
                        spot = nav;
                        found = true;
                    }
                    if (found) return true;
                }
            }
            return false;
        }

        // A seat or a way home that filled up meanwhile: the free spot beside it (the same point when it is free).
        public Vector3 ClearSeat(Crew who, Vector3 seat, IList<Vector3> claimed = null, bool othersStay = true)
        {
            if (!KeepClear || !Begun) return seat;
            var others = othersStay ? OthersStanding(who) : new List<Vector3>();
            if (claimed != null) others.AddRange(claimed);
            if (StandClear(who, seat) && others.All(o => Mathf.Abs(o.y - seat.y) > .2f || Flat(o, seat) >= GatherFormation.Settings.MinSpacing)) return seat;
            return FreeStand(who, seat, .32f, out var free, claimed, false, othersStay) ? free : seat;
        }

        public Vector3 ClearSeat(Pet pet, Vector3 seat, IList<Vector3> claimed = null, bool othersStay = true)
        {
            foreach (var pair in cast) if (pair.Value != null && pair.Value.movement == pet) return ClearSeat(pair.Key, seat, claimed, othersStay);
            return seat;
        }

        private bool CrewOf(Pet pet, out Crew who)
        {
            foreach (var pair in cast) if (pair.Value != null && pair.Value.movement == pet) { who = pair.Key; return true; }
            who = default;
            return false;
        }

        // A free patch of desk for a small thing the session puts down (an idea note): nearest `want`.
        // `alsoTaken`: where other notes are about to stand (they are on their way).
        private bool FreeDeskSpot(Vector3 want, float radius, Transform self, out Vector3 spot, IList<Vector3> alsoTaken = null)
        {
            spot = want;
            var feet = Footprints();
            // It stays on the surface it was meant for (never over the edge, where the next surface down is the floor).
            var surface = SurfaceAt(want + Vector3.up * .03f);
            if (surface == null) return false;
            for (float r = 0f; r <= .4f; r += .03f)
            {
                int steps = r < .01f ? 1 : Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * r / .04f), 8, 24);
                for (int i = 0; i < steps; i++)
                {
                    float a = i * 2f * Mathf.PI / steps;
                    Vector3 p = want + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                    var me = Round(FootKind.IdeaNote, "", null, p, radius, .08f);
                    bool ok = true;
                    foreach (var f in feet)
                    {
                        if (f.owner == self && self != null) continue;
                        if (f.kind == FootKind.Real) continue; // a note leans against its thing
                        if (FootGap(me, f) < (f.kind == FootKind.Crew ? .02f : .008f)) { ok = false; break; }
                    }
                    if (!ok) continue;
                    if (alsoTaken != null && alsoTaken.Any(t => Mathf.Abs(t.y - p.y) < .2f && Flat(t, p) < radius * 2f + .008f)) continue;
                    if (r > .01f && !surface.Contains(new Vector2(p.x, p.z), radius)) continue;
                    spot = p;
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------ looking again, all the time

        private bool ClearanceExempt(Crew who, Pet pet)
        {
            if (steppingAside.Contains(who) || walkingBack.Contains(pet) || stranded.Contains(pet)) return true;
            if (who == Crew.Spark && SparkWalking) return true;          // on a tour: it comes back by itself
            // At its work (the work space keeps the others away), and just after: it walks back to its seat by itself.
            if (who == Crew.Maker && (MakerWorking || OfferingBook || Time.time - makerBusyAt < MakerAfterWork)) return true;
            if (Comparing || InMeeting) return true;                     // voting / sticking notes: they walk back after
            if (arrival != null && arrival.CallingOver) return true;
            return false;
        }

        private void ClearanceFrame()
        {
            if (Time.time < nextClearance) return;
            nextClearance = Time.time + .25f;
            UpdateThingBlockers();
            if (MakerWorking || OfferingBook) makerBusyAt = Time.time;
            if (!KeepClear) return;
            var feet = FootprintsInto(footBuffer, true);
            // Who has come to rest (standing or seated, in one place for SettledFor).
            var settled = settledNow;
            settled.Clear();
            foreach (var pair in cast)
            {
                var c = pair.Value;
                if (c == null || c.movement == null) continue;
                var pet = c.movement;
                bool still = !pet.IsHeld && (pet.MeetingSeatLocked || pet.State == Pet.MotionState.Idle);
                if (!still) { stoodAt.Remove(pair.Key); }
                else
                {
                    if (!stoodAt.TryGetValue(pair.Key, out var was) || Vector3.Distance(was.at, c.transform.position) > .01f) stoodAt[pair.Key] = (c.transform.position, Time.time);
                    else if (Time.time - was.since >= SettledFor) settled.Add(pair.Key);
                }
                // At rest on something that is not the room (the top of a made thing): it never lands by itself.
                bool resting = !pet.IsHeld && (pet.State == Pet.MotionState.Falling || pet.State == Pet.MotionState.Recovering) &&
                               pet.TryGetComponent<Rigidbody>(out var body) && !body.isKinematic && body.linearVelocity.magnitude < .08f;
                if (!resting) restingSince.Remove(pair.Key);
                else if (!restingSince.ContainsKey(pair.Key)) restingSince[pair.Key] = Time.time;
            }
            NoteWalkThroughs(feet);

            // One teammate per look (a move changes the map for the next).
            bool moved = false;
            foreach (var pair in cast)
            {
                if (moved) break;
                var c = pair.Value;
                if (c == null || c.movement == null) continue;
                var pet = c.movement;
                if (nextClearTry.TryGetValue(pair.Key, out float next) && Time.time < next) continue;
                if (restingSince.TryGetValue(pair.Key, out float since) && Time.time - since > 1.2f && !ClearanceExempt(pair.Key, pet))
                {
                    moved = HopDown(pair.Key, c);
                    nextClearTry[pair.Key] = Time.time + (moved ? 2.5f : 1.5f);
                    continue;
                }
                if (!settled.Contains(pair.Key)) continue;
                var body = BodyAt(pair.Key, c.transform.position);
                Foot worst = default;
                float depth = 0f;
                bool friend = false;
                foreach (var f in feet)
                {
                    if (f.kind == FootKind.Crew)
                    {
                        if (f.crew == pair.Key || !settled.Contains(f.crew)) continue;
                        // Friends keep the crew's minimum spacing, middle to middle.
                        float into = GatherFormation.Settings.MinSpacing - CrewSlack - (FootGap(body, f) + 2f * CrewBody);
                        if (FootGap(body, f) == float.MaxValue || into <= depth) continue;
                        depth = into; worst = f; friend = true;
                    }
                    else
                    {
                        if (f.kind == FootKind.Real || f.kind == FootKind.Notebook || Belongs(pair.Key, f)) continue;
                        float gap = FootGap(body, f);
                        float into = ThingClear - .004f - gap;
                        if (gap == float.MaxValue || into <= depth) continue;
                        depth = into; worst = f; friend = false;
                    }
                }
                if (depth <= 0f) continue;
                string key = pair.Key + "|" + worst.name;
                bool exempt = ClearanceExempt(pair.Key, pet) || LessonPinned(c);
                // Of two friends, the one that came to rest last moves (unless it may not).
                if (friend && !exempt && cast.TryGetValue(worst.crew, out var other) && other != null &&
                    stoodAt.TryGetValue(worst.crew, out var theirs) && stoodAt.TryGetValue(pair.Key, out var mine) && theirs.since > mine.since &&
                    !ClearanceExempt(worst.crew, other.movement) && !LessonPinned(other)) continue;
                NoteOverlap(key, MakerRoles.Name(pair.Key) + " overlaps " + worst.name + " by " + (depth * 100f).ToString("F1") + " cm at " + c.transform.position.ToString("F2") + (exempt ? " (busy: left)" : ""));
                if (exempt) continue;
                // Inside or on something with height: hop out. Else walk.
                bool hop = !friend && (worst.kind == FootKind.Made || worst.kind == FootKind.Real || worst.kind == FootKind.Notebook) && worst.top > .012f;
                if (!FreeStand(pair.Key, c.transform.position, .45f, out var spot, null, !hop))
                {
                    // No walk out: any free spot it can hop to.
                    if (hop || !FreeStand(pair.Key, c.transform.position, .45f, out spot, null, false))
                    {
                        NoteOverlap(key + "|stuck", MakerRoles.Name(pair.Key) + " has no free spot near " + c.transform.position.ToString("F2"));
                        nextClearTry[pair.Key] = Time.time + 2f;
                        continue;
                    }
                    hop = true;
                }
                StartCoroutine(MoveClear(pair.Key, c, spot, hop, worst.name));
                nextClearTry[pair.Key] = Time.time + 1.5f;
                moved = true;
            }
            if (!moved) SlideNotes(feet);
            NoteThingOverlaps(feet);
        }

        private void NoteOverlap(string key, string line)
        {
            if (overlapNoted.TryGetValue(key, out float at) && Time.time - at < 6f) return;
            overlapNoted[key] = Time.time;
            OverlapLog.Add(line);
            if (OverlapLog.Count > 200) OverlapLog.RemoveAt(0);
            WriteInputLog("overlap " + Current + " | " + line);
            Debug.Log("[Windups Clear] " + line);
        }

        // A walker's body going through a friend that stands still (the seat obstacles are there to stop this).
        private void NoteWalkThroughs(List<Foot> feet)
        {
            var now = walkNow;
            now.Clear();
            foreach (var a in feet)
            {
                if (a.kind != FootKind.Crew || !cast.TryGetValue(a.crew, out var walker) || walker == null) continue;
                var state = walker.movement.State;
                if (state != Pet.MotionState.Walking) continue;
                foreach (var b in feet)
                {
                    if (b.kind != FootKind.Crew || b.crew == a.crew || !cast.TryGetValue(b.crew, out var o) || o == null || o.movement.State == Pet.MotionState.Walking) continue;
                    if (FootGap(a, b) > -.03f) continue; // bodies more than 3 cm into each other
                    string key = a.crew + ">" + b.crew;
                    now.Add(key);
                    if (insideNow.Contains(key)) continue;
                    WalkThroughs++;
                    NoteOverlap("walk|" + key, MakerRoles.Name(a.crew) + " walks through " + MakerRoles.Name(b.crew) + " (" + (-FootGap(a, b) * 100f).ToString("F1") + " cm)");
                }
            }
            insideNow.Clear();
            foreach (var k in now) insideNow.Add(k);
        }

        private IEnumerator MoveClear(Crew who, WindupCharacter c, Vector3 spot, bool hop, string from)
        {
            steppingAside.Add(who);
            headingTo[who] = spot;
            try
            {
                var pet = c.movement;
                // (Standing inside a thing: its obstacle stands aside first, or the navigation would shove the
                // character out sideways the moment it is switched on.)
                if (hop) Pet.MakeWay(c.transform.position);
                pet.UnlockMeetingSeat();
                yield return null;
                bool going = false;
                if (!hop) going = pet.GoTo(spot, 2f, true);
                if (!going)
                {
                    // (a landing takes half a second before it can hop)
                    for (float wait = Time.time + 1.5f; Time.time < wait && !pet.IsHeld && pet.State != Pet.MotionState.Idle && pet.State != Pet.MotionState.Falling && pet.State != Pet.MotionState.Recovering; ) yield return null;
                    going = pet.HopTo(spot);
                    if (going) ClearHops++;
                }
                else ClearMoves++;
                if (going)
                {
                    Debug.Log("[Windups Clear] " + MakerRoles.Name(who) + (hop ? " hops" : " walks") + " clear of " + from + " to " + spot.ToString("F2"));
                    WriteInputLog("overlap fix | " + MakerRoles.Name(who) + (hop ? " hops" : " walks") + " clear of " + from);
                    yield return null;
                    for (float until = Time.time + 5f; Time.time < until && pet.State != Pet.MotionState.Idle && !pet.IsHeld; ) yield return null;
                }
                for (int i = 0; i < 30 && !pet.MeetingSeatLocked && !pet.IsHeld && pet.State == Pet.MotionState.Idle; i++) { pet.LockMeetingSeat(); yield return null; }
            }
            finally
            {
                steppingAside.Remove(who);
                headingTo.Remove(who);
            }
        }

        // At rest on top of something: hop down to the nearest free spot of a walkable surface below or beside it.
        private bool HopDown(Crew who, WindupCharacter c)
        {
            var room = arrival.handoff.meeting.environment;
            if (room == null) return false;
            Vector3 at = c.transform.position;
            int mask = room.GeometryMask;
            var levels = new List<Vector3>();
            for (int ring = 0; ring < 4; ring++)
                for (int i = 0; i < (ring == 0 ? 1 : 8); i++)
                {
                    Vector3 over = at + (ring == 0 ? Vector3.zero : Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * (ring * .1f)) + Vector3.up * .05f;
                    if (Physics.Raycast(over, Vector3.down, out var hit, 1.6f, mask, QueryTriggerInteraction.Ignore) && hit.normal.y > .9f &&
                        !levels.Any(l => Mathf.Abs(l.y - hit.point.y) < .03f && Flat(l, hit.point) < .05f)) levels.Add(hit.point);
                }
            foreach (var level in levels.OrderBy(l => Vector3.Distance(l, at)))
            {
                if (!FreeStand(who, level, .3f, out var spot, null, false)) continue;
                if (!c.movement.HopTo(spot, Mathf.Clamp(.03f + .15f * Flat(spot, at), .04f, .09f))) continue;
                ClearHops++;
                NoteOverlap(who + "|perched", MakerRoles.Name(who) + " was resting on something at " + at.ToString("F2") + " and hops down to " + spot.ToString("F2"));
                StartCoroutine(SeatAfterHop(who, c));
                return true;
            }
            return false;
        }

        private IEnumerator SeatAfterHop(Crew who, WindupCharacter c)
        {
            steppingAside.Add(who);
            try
            {
                var pet = c.movement;
                yield return null;
                for (float until = Time.time + 4f; Time.time < until && pet.State != Pet.MotionState.Idle && !pet.IsHeld; ) yield return null;
                for (int i = 0; i < 30 && !pet.MeetingSeatLocked && !pet.IsHeld && pet.State == Pet.MotionState.Idle; i++) { pet.LockMeetingSeat(); yield return null; }
            }
            finally { steppingAside.Remove(who); }
        }

        // An idea note with a made thing, the mic or a teammate's seat on it: it slides to the nearest free patch.
        private void SlideNotes(List<Foot> feet)
        {
            foreach (var idea in Ideas)
            {
                if (idea == null || idea.root == null || !idea.placed || sliding.Contains(idea.root)) continue;
                int at = feet.FindIndex(f => f.owner == idea.root);
                if (at < 0) continue;
                var mine = feet[at];
                Foot worst = default;
                float depth = 0f;
                foreach (var f in feet)
                {
                    if (f.owner == idea.root || f.kind == FootKind.Real || f.kind == FootKind.Crew || f.kind == FootKind.Notebook) continue;
                    if (f.kind == FootKind.IdeaNote && string.CompareOrdinal(f.name, mine.name) > 0) continue; // of two notes, one moves
                    float gap = FootGap(mine, f);
                    if (gap == float.MaxValue || -gap <= depth + .004f) continue;
                    depth = -gap; worst = f;
                }
                if (depth <= 0f) continue;
                NoteOverlap(mine.name + "|" + worst.name, mine.name + " overlaps " + worst.name + " by " + (depth * 100f).ToString("F1") + " cm");
                Vector3 foot = new Vector3(mine.at.x, mine.at.y, mine.at.z);
                if (!FreeDeskSpot(foot, NoteFoot, idea.root, out var free)) continue;
                Vector3 shift = free - foot;
                shift.y = 0f;
                if (shift.magnitude < .005f) continue;
                NoteSlides++;
                idea.home += shift;
                StartCoroutine(SlideNote(idea.root, idea.root.position + shift));
                return;
            }
        }

        private readonly HashSet<Transform> sliding = new HashSet<Transform>();
        private IEnumerator SlideNote(Transform note, Vector3 to)
        {
            sliding.Add(note);
            Vector3 from = note.position;
            for (float t = 0f; t < 1f && note != null; t += Time.deltaTime / .35f)
            {
                note.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t));
                yield return null;
            }
            if (note != null) note.position = to;
            sliding.Remove(note);
        }

        // ------------------------------------------------------------ walking round the things made

        private void UpdateThingBlockers()
        {
            var things = MadeThings();
            foreach (var t in things)
            {
                if (t == placementView || thingBlockers.ContainsKey(t)) continue;
                var mesh = t.GetComponentInChildren<MeshRenderer>();
                if (mesh == null) continue;
                var obstacle = Pet.AddBlocker(mesh.gameObject);
                var lb = mesh.localBounds;
                Vector3 s = mesh.transform.lossyScale;
                float sx = Mathf.Max(1e-4f, Mathf.Abs(s.x)), sy = Mathf.Max(1e-4f, Mathf.Abs(s.y)), sz = Mathf.Max(1e-4f, Mathf.Abs(s.z));
                var wb = mesh.bounds;
                float wide = Mathf.Max(wb.extents.x, wb.extents.z), narrow = Mathf.Min(wb.extents.x, wb.extents.z);
                obstacle.center = lb.center;
                if (wide <= narrow * 1.6f)
                {
                    // Round (a walker slides round a circle; head-on against a flat box face it just stops). A little
                    // inside its real edge: Maker stands right by its work, and a walker may brush past.
                    obstacle.shape = NavMeshObstacleShape.Capsule;
                    obstacle.radius = Mathf.Max(.01f, wide - .006f) / Mathf.Max(sx, sz);
                    obstacle.height = Mathf.Max(.02f, wb.size.y) / sy;
                }
                else
                {
                    obstacle.shape = NavMeshObstacleShape.Box;
                    obstacle.size = new Vector3(Mathf.Max(.004f, lb.size.x - .012f / sx), lb.size.y, Mathf.Max(.004f, lb.size.z - .012f / sz));
                }
                thingBlockers[t] = obstacle;
            }
            bool anyGone = false;
            foreach (var pair in thingBlockers)
            {
                if (pair.Key == null || pair.Value == null) { anyGone = true; continue; }
                // Not in a hand, not while Maker works on it (it stands right against its work), and only when it
                // stands on something (a thing left in the air over the desk is in nobody's way).
                bool on = Pet.SeatBlockers && pair.Key != held && Sculpt == SculptStage.None && pair.Key.gameObject.activeInHierarchy && !Pet.LetThrough(pair.Value) && StandsOnSurface(pair.Value);
                if (pair.Value.enabled != on) pair.Value.enabled = on;
            }
            if (anyGone) foreach (var gone in thingBlockers.Keys.Where(k => k == null).ToList()) thingBlockers.Remove(gone);
        }

        // Its underside within 7 cm of a room surface below it.
        private bool StandsOnSurface(NavMeshObstacle obstacle)
        {
            if (!obstacle.TryGetComponent<MeshRenderer>(out var mesh)) return true;
            var b = mesh.bounds;
            return Physics.Raycast(new Vector3(b.center.x, b.min.y + .03f, b.center.z), Vector3.down, .1f, arrival.handoff.meeting.environment.GeometryMask, QueryTriggerInteraction.Ignore);
        }

        // ------------------------------------------------------------ tests

        // Every overlap on the map right now: (who, what, how deep in metres). Crew pairs under the minimum
        // spacing, a teammate in a thing, an idea note under a thing.
        public List<(string a, string b, float depth)> OverlapsNow(bool settledOnly = true)
        {
            var list = new List<(string, string, float)>();
            var feet = Footprints();
            for (int i = 0; i < feet.Count; i++)
                for (int j = i + 1; j < feet.Count; j++)
                {
                    var a = feet[i];
                    var b = feet[j];
                    if (a.kind != FootKind.Crew && b.kind == FootKind.Crew) { var swap = a; a = b; b = swap; }
                    float gap = FootGap(a, b);
                    if (gap == float.MaxValue) continue;
                    if (a.kind == FootKind.Crew)
                    {
                        if (!cast.TryGetValue(a.crew, out var ca) || ca == null) continue;
                        bool stillA = ca.movement.MeetingSeatLocked || ca.movement.State == Pet.MotionState.Idle;
                        if (settledOnly && !stillA) continue;
                        if (b.kind == FootKind.Crew)
                        {
                            if (!cast.TryGetValue(b.crew, out var cb) || cb == null) continue;
                            if (settledOnly && !(cb.movement.MeetingSeatLocked || cb.movement.State == Pet.MotionState.Idle)) continue;
                            float into = GatherFormation.Settings.MinSpacing - CrewSlack - (gap + 2f * CrewBody);
                            if (into > 0f) list.Add((a.name, b.name, into));
                        }
                        else if (b.kind != FootKind.Real && b.kind != FootKind.Notebook && !Belongs(a.crew, b) && ThingClear - .004f - gap > 0f) list.Add((a.name, b.name, ThingClear - .004f - gap));
                    }
                    else
                    {
                        float into = ThingsInto(a, b);
                        if (into > 0f) list.Add((a.name, b.name, into));
                    }
                }
            return list;
        }

        // Two things (no teammate): how deep they overlap beyond what is meant (0: fine or meant). Meant: the
        // note in its ring, a note leaning on its real thing, a real thing in the show-me circle or under the
        // thing made for it, the notebook (it follows Maker), a flag by v2, a thing on its stand. Two things the person placed count
        // only when clearly inside each other (their boxes are a little bigger than their shapes).
        private float ThingsInto(in Foot a, in Foot b)
        {
            var x = a.kind <= b.kind ? a : b;
            var y = a.kind <= b.kind ? b : a;
            if (x.kind == FootKind.Crew || y.kind == FootKind.Crew) return 0f;
            if (x.kind == FootKind.Notebook || y.kind == FootKind.Notebook) return 0f;
            // (F52) A made thing on its display stand is where it belongs.
            if (x.kind == FootKind.Made && y.kind == FootKind.Made && IsStand(x.owner) != IsStand(y.owner) &&
                (IsStand(x.owner) ? StandsOn(y.owner, x.owner) : StandsOn(x.owner, y.owner))) return 0f;
            float gap = FootGap(x, y);
            if (gap == float.MaxValue) return 0f;
            switch (x.kind)
            {
                case FootKind.Made:
                    if (y.kind == FootKind.Made) return -gap - .02f;
                    if (y.kind == FootKind.Ring || y.kind == FootKind.Flag || y.kind == FootKind.Real) return 0f;
                    return -gap - .004f;
                case FootKind.IdeaNote:
                    if (y.kind == FootKind.Real) return 0f;
                    return -gap - .004f;
                case FootKind.RingNote:
                    if (y.kind == FootKind.Ring || y.kind == FootKind.Flag || y.kind == FootKind.Real) return 0f;
                    return -gap - .004f;
                case FootKind.Ring:
                    if (y.kind == FootKind.Flag || y.kind == FootKind.Real) return 0f;
                    return -gap - .004f;
                case FootKind.Mic:
                    return -gap - .004f;
                case FootKind.Page:
                case FootKind.Board:
                    if (y.kind == FootKind.Flag || y.kind == FootKind.Real) return 0f;
                    return -gap - .004f;
                case FootKind.Flag:
                    if (y.kind == FootKind.Real) return 0f;
                    return -gap - .004f;
                case FootKind.Circle:
                    return 0f; // (with Real: that is what the circle is for)
            }
            return 0f;
        }

        // The things that overlap each other are written down too (the session's own placements should never;
        // a thing the person put somewhere stays there).
        private void NoteThingOverlaps(List<Foot> feet)
        {
            for (int i = 0; i < feet.Count; i++)
                for (int j = i + 1; j < feet.Count; j++)
                {
                    float into = ThingsInto(feet[i], feet[j]);
                    if (into <= .006f) continue;
                    NoteOverlap(feet[i].name + "|" + feet[j].name, feet[i].name + " overlaps " + feet[j].name + " by " + (into * 100f).ToString("F1") + " cm at " + feet[j].at.ToString("F2"));
                }
        }

        public bool SimulateDropOn(Crew crew, Vector3 at)
        {
            if (!cast.TryGetValue(crew, out var c) || c == null) return false;
            var pet = c.movement;
            pet.UnlockMeetingSeat();
            bool placed = pet.TryPlaceAt(at);
            pet.LockMeetingSeat();
            stoodAt.Remove(crew);
            nextClearTry.Remove(crew);
            return placed;
        }
    }
}
