using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Pet = RoomCapsule.CapsulePetController;

namespace Windups.Handoff
{
    // F89 (2026-11-15, the user: "while Maker is working on the stand, when I move the stand, Maker and its work should
    // come along"). A stand carried only the made things standing on it (F52), so lifting it while Maker carved or
    // painted on it left the wood block, the shape and the paint pot hanging where they were and Maker working at
    // nothing. Now, when Maker's work is on the stand (its sculpt spot on the stand's top):
    //  - grabbed, everything of the work goes along as on a tray: the block, the shape and its paint, the paint pot, the
    //    size box outline, the hidden made thing being repainted, and the places the make will land (sculpt spot, the
    //    approved place, the version spot); Maker itself, standing still at its work (seated), comes along at its place
    //    beside the stand and keeps working;
    //  - let go (after the stand settles onto a surface), Maker stands right where it was carried to if that is a free
    //    walkable spot beside the work; else on another side of the work (WalkToWork's rules, without the walk); else
    //    it gets down nearby and asks for the stand to go where it can reach. Maker walking to its work is sent on to
    //    the work's new place.
    public sealed partial class MakerSession
    {
        public bool WorkCarried => workCarried;
        public int WorkCarries { get; private set; }
        public int WorkLandings { get; private set; }
        public string LastWorkLanding { get; private set; } = "";

        private bool workCarried, carryMaker, carryApprovedOn, carryMugOn, carryBook;
        private Pose carryBookPose;
        private Vector3 carryBookAnchor;
        private Transform workStand;
        private Vector3 workFrom, carrySpot, carryStandPoint, carryApproved, carryMug, carriedPetFrom;
        private Vector3? carryVersion;
        private Quaternion workFromTurn, carrySculptTurn, carryBlockTurn, carryApprovedTurn, carryMugTurn, carryVersionTurn, carriedPetTurn;
        private readonly List<(Transform thing, Vector3 position, Quaternion rotation)> workPieces = new List<(Transform, Vector3, Quaternion)>();
        private Pet carriedPet;

        private bool OnStandTop(Transform stand, Vector3 p, float above = .03f)
        {
            Vector3 top = StandTopOf(stand);
            return p.y - top.y < above && top.y - p.y < .03f && new Vector2(p.x - top.x, p.z - top.z).magnitude <= StandRadiusOf(stand);
        }

        // Grabbed (PickRiders): Maker's work on this stand comes along.
        private void PickWork(Transform stand)
        {
            workCarried = carryMaker = false;
            carriedPet = null;
            workPieces.Clear();
            if (Sculpt == SculptStage.None || stand == null || !OnStandTop(stand, sculptSpot)) return;
            workCarried = true;
            workStand = stand;
            workFrom = stand.position;
            workFromTurn = stand.rotation;
            WorkCarries++;
            void Piece(Transform t, bool evenHidden = false)
            {
                if (t != null && (evenHidden || t.gameObject.activeInHierarchy) && !workPieces.Any(w => w.thing == t)) workPieces.Add((t, t.position, t.rotation));
            }
            Piece(block);
            Piece(view);
            Piece(pot);
            Piece(sculptFocus);
            if (sizeBox != null && OnStandTop(stand, sizeBox.position, .3f)) Piece(sizeBox);
            if (hiddenProduct != null && OnStandTop(stand, hiddenProduct.position)) Piece(hiddenProduct, true);
            carrySpot = sculptSpot;
            carryStandPoint = standPoint;
            carrySculptTurn = sculptTurn;
            carryBlockTurn = blockTurn;
            carryApprovedOn = OnStandTop(stand, approvedPlace);
            carryApproved = approvedPlace;
            carryApprovedTurn = approvedTurn;
            carryMugOn = OnStandTop(stand, MugSpot);
            carryMug = MugSpot;
            carryMugTurn = MugTurn;
            carryVersion = versionSpot.HasValue && OnStandTop(stand, versionSpot.Value) ? versionSpot : null;
            carryVersionTurn = versionTurn;
            if (cast.TryGetValue(Crew.Maker, out var maker) && maker != null && maker.movement.MeetingSeatLocked && !maker.movement.IsHeld)
            {
                carriedPet = maker.movement;
                carryMaker = true;
                carriedPetFrom = carriedPet.transform.position;
                carriedPetTurn = carriedPet.transform.rotation;
                // Its closed notebook lying beside it comes along too (else it is put down again beside Maker, which
                // may be on the stand's rim).
                carryBook = closedBookWasShown && !notebookPlaced;
                carryBookPose = closedBookPose;
                carryBookAnchor = closedBookAnchor;
            }
            WriteInputLog("stand: carried with Maker's work (" + Sculpt + ", " + workPieces.Count + " pieces" + (carryMaker ? ", Maker comes along" : ", Maker not at its place") + ")", true);
        }

        // Every frame the stand moves (CarryRiders): the work and Maker keep their places on and beside it.
        private void CarryWork(Transform stand)
        {
            if (!workCarried || stand != workStand) return;
            Quaternion turn = stand.rotation * Quaternion.Inverse(workFromTurn);
            Vector3 Move(Vector3 p) => stand.position + turn * (p - workFrom);
            foreach (var (thing, position, rotation) in workPieces)
            {
                if (thing == null) continue;
                thing.SetPositionAndRotation(Move(position), turn * rotation);
                SyncBody(thing);
            }
            sculptSpot = Move(carrySpot);
            standPoint = Move(carryStandPoint);
            sculptTurn = turn * carrySculptTurn;
            blockTurn = turn * carryBlockTurn;
            if (carryApprovedOn) { approvedPlace = Move(carryApproved); approvedTurn = turn * carryApprovedTurn; }
            if (carryMugOn) { MugSpot = Move(carryMug); MugTurn = turn * carryMugTurn; }
            if (carryVersion.HasValue) { versionSpot = Move(carryVersion.Value); versionTurn = turn * carryVersionTurn; }
            if (carryMaker && carriedPet != null) carriedPet.MoveSeated(Move(carriedPetFrom), turn * carriedPetTurn);
            if (carryMaker && carryBook && !notebookPlaced)
            {
                closedBookPose = new Pose(Move(carryBookPose.position), turn * carryBookPose.rotation);
                closedBookAnchor = Move(carryBookAnchor);
                if (notebookClosed != null) notebookClosed.SetPositionAndRotation(closedBookPose.position, closedBookPose.rotation);
            }
        }

        // Let go and settled (StandLetGo / SettleStand): Maker is set down beside its work.
        private void LandWork(Transform stand)
        {
            if (!workCarried || stand != workStand) return;
            CarryWork(stand);
            workCarried = false;
            if (Sculpt == SculptStage.None || !cast.TryGetValue(Crew.Maker, out var maker) || maker == null) return;
            var pet = maker.movement;
            var room = arrival.handoff.meeting.environment;
            if (carryMaker && pet.MeetingSeatLocked && !pet.IsHeld)
            {
                if (StandsBesideWork(pet.transform.position, out Vector3 here))
                {
                    pet.MoveSeated(here, pet.transform.rotation);
                    standPoint = here;
                    Landed("where it was carried to");
                    return;
                }
                if (WorkPointNear(maker, out Vector3 side, out Vector3 toWork))
                {
                    pet.MoveSeated(side, Quaternion.LookRotation(toWork, Vector3.up));
                    standPoint = side;
                    NoteWorkAngle();
                    if (Sculpt == SculptStage.Clay && block != null && block.gameObject.activeSelf) { blockTurn = Quaternion.LookRotation(-toWork, Vector3.up); StartCoroutine(TurnTo(block, blockTurn, .25f)); }
                    if (pot != null && pot.gameObject.activeSelf) PlacePot(maker);
                    ToyFx.Puff(side + Vector3.up * .01f, .006f, 2);
                    Landed("on another side of the work");
                    return;
                }
                // Nowhere to stand beside it: Maker gets down near it (or falls to what is below) and asks.
                pet.UnlockMeetingSeat();
                if (room != null && room.TrySameLevelNavPoint(sculptSpot, out var nav) && Vector3.ProjectOnPlane(nav - sculptSpot, Vector3.up).magnitude < .4f) pet.SettleAt(nav);
                Say(Crew.Maker, "I can't stand next to it there. Put it where I can reach?");
                Landed("no place beside it");
                return;
            }
            // Maker was on its way to the work: on to its new place.
            if (Sculpt == SculptStage.Approach && pet.State == Pet.MotionState.Walking && Walkable(pet.transform.position, standPoint, 2.5f))
            {
                pet.GoTo(standPoint, 2.5f, true);
                Landed("walking on to the new place");
            }
        }

        private void Landed(string how)
        {
            WorkLandings++;
            LastWorkLanding = how;
            WriteInputLog("stand: put down with Maker's work; Maker " + how, true);
        }

        // A free walkable spot right here, on the work's level, where Maker may stand.
        private bool StandsBesideWork(Vector3 at, out Vector3 nav)
        {
            nav = at;
            var room = arrival.handoff.meeting.environment;
            if (room == null || !room.TrySameLevelNavPoint(at, out nav)) return false;
            return Vector3.ProjectOnPlane(nav - at, Vector3.up).magnitude < .02f && Mathf.Abs(nav.y - at.y) < .03f &&
                   Mathf.Abs(nav.y - sculptSpot.y) < .04f && MakerStandClear(nav);
        }

        // Another side of the work to stand on (WalkToWork's choice, without the walk: Maker is set down there).
        private bool WorkPointNear(WindupCharacter maker, out Vector3 point, out Vector3 toWork)
        {
            point = toWork = Vector3.zero;
            var room = arrival.handoff.meeting.environment;
            if (room == null) return false;
            bool shape = view != null && view.gameObject.activeSelf && viewMesh != null;
            Mesh mesh = shape ? viewMesh : blockMesh;
            if (mesh == null) return false;
            float radius = shape ? Mathf.Max(view.GetComponent<Renderer>().bounds.extents.x, view.GetComponent<Renderer>().bounds.extents.z) + .03f : blockHalf + .01f;
            Vector3 scale = shape ? Vector3.one : BlockScale(1f, 1f);
            var sides = new List<Vector3>();
            Vector3 from = Vector3.ProjectOnPlane(maker.transform.position - sculptSpot, Vector3.up);
            Vector3 dir = from.sqrMagnitude > 1e-4f ? from.normalized : Vector3.forward;
            if (Head != null)
            {
                Vector3 toPerson = Vector3.ProjectOnPlane(Head.position - sculptSpot, Vector3.up).normalized;
                foreach (float angle in new[] { WorkSideAngle, 90f })
                {
                    sides.Add(Quaternion.Euler(0f, angle, 0f) * toPerson);
                    sides.Add(Quaternion.Euler(0f, -angle, 0f) * toPerson);
                }
            }
            foreach (float turnBy in new[] { 0f, 30f, -30f, 60f, -60f, 95f, -95f, 140f, -140f, 180f }) sides.Add(Quaternion.Euler(0f, turnBy, 0f) * dir);
            float keep = MakerWorkRoom + CrewBodyRadius;
            foreach (var d in sides)
            {
                var turn = shape ? sculptTurn : Quaternion.LookRotation(d, Vector3.up);
                float near = Mathf.Max(radius + .045f, ClearStand(maker, mesh, sculptSpot, turn, scale, d, radius + .045f));
                foreach (float extra in new[] { 0f, .01f, .02f })
                {
                    Vector3 want = sculptSpot + d * (near + extra);
                    if (!room.TrySameLevelNavPoint(want, out var nav) || Mathf.Abs(nav.y - sculptSpot.y) > .04f) continue;
                    if (Vector3.ProjectOnPlane(nav - want, Vector3.up).magnitude > .012f || Vector3.ProjectOnPlane(nav - sculptSpot, Vector3.up).magnitude < near - .004f) continue;
                    if (cast.Values.Any(c => c != null && c != maker && Vector3.ProjectOnPlane(c.transform.position - nav, Vector3.up).magnitude < keep)) continue;
                    if (!MakerStandClear(nav)) continue;
                    point = nav;
                    toWork = -d;
                    return true;
                }
            }
            return false;
        }

        // Checks (VerifyF89.Stand): Maker's work and its stand.
        public Vector3 MakerStandPoint => standPoint;
        public Transform WorkStand => OnStandTopAny(sculptSpot);
        private Transform OnStandTopAny(Vector3 p)
        {
            foreach (var s in Stands) if (s != null && OnStandTop(s, p)) return s;
            return null;
        }
    }
}
