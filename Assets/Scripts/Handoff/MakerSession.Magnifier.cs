using UnityEngine;

namespace Windups.Handoff
{
    public sealed partial class MakerSession
    {
        private float magnifierLift;
        public Transform Magnifier => magnifier;
        public Vector3 MagnifierGrip => magnifier != null ? magnifier.TransformPoint(MagnifierHandle) : Vector3.zero;
        // (2026-11-13, the user: "Spark's magnifier overlaps its face") Held up as a tool it used to stand right in front
        // of the face as the person sees it (only going through the head was checked); then it was held beside the
        // face, low and laid sideways, which looked odd ("the pose looking at the magnifier is weird"). Now Spark
        // peers through it at a spot on its surface ahead (Peer): the lens on that line of sight, below the face as
        // the person sees it.
        public static float FaceClearDegrees = 2f;
        public float MagnifierFaceGap { get; private set; } // degrees between the lens's edge and the face's edge, seen by the person
        private void MagnifierFrame()
        {
            magnifierOwnsLook = false;
            if (magnifier == null || !cast.TryGetValue(Crew.Spark, out var spark) || spark == null) return;
            var body = Body(Crew.Spark);
            var hand = body?.RigHand(true);
            var pet = spark.movement;
            bool shown = ToolShown(Crew.Spark) || introTool == "magnifier"; // F55: held up toward the person when a hand comes near (a tap = look around)
            bool want = (looking || SparkWalking || shown) && hand != null && pet != null && !pet.IsHeld && !(pet.State == RoomCapsule.CapsulePetController.MotionState.Falling);
            bool walking = pet != null && (pet.State == RoomCapsule.CapsulePetController.MotionState.Walking);
            magnifierLift = Mathf.MoveTowards(magnifierLift, want ? 1f : 0f, Time.deltaTime * 4f);
            if (!want && magnifierLook != null && spark.lookTarget == magnifierLook) spark.lookTarget = Head; // done peering: back to the person
            if (magnifierLift <= 0f)
            {
                if (magnifier.gameObject.activeSelf) ToyFx.Puff(magnifier.position,.006f,2);
                magnifier.gameObject.SetActive(false); body?.Hold(true,null); return;
            }
            // Held/falling characters must not fight their grab reaction pose.
            if (pet.IsHeld || (pet.State == RoomCapsule.CapsulePetController.MotionState.Falling)) { magnifierLift=0; magnifier.gameObject.SetActive(false); body?.Hold(true,null); return; }
            magnifier.gameObject.SetActive(true);
            CacheMagnifierPalm(body,hand);
            Vector3 origin = spark.transform.position;
            Vector3 forward = Vector3.ProjectOnPlane(body.transform.forward,Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 grip;
            Quaternion turn;
            if (walking)
            {
                // Carry it clear of the belly. A distant focus may be behind Spark during a turn;
                // aiming at that focus while walking twists the ring through the body.
                Vector3 offset = Vector3.ClampMagnitude(forward * .048f + right * .022f, HandReach - .005f);
                grip = origin + offset + Vector3.up * Mathf.Lerp(.045f, .050f, magnifierLift);
                turn = Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Euler(18f, 0, 0); // tilted away from the face
            }
            else if (shown && !looking && !SparkWalking)
            {
                // Shown to the person (the introduction, a hand near Spark): held up beside its head, the glass toward
                // the person, the handle tipped in to the mitten; Spark looks at the person (CrewToolsLate).
                Present(body, origin, forward, right, out grip, out turn);
                MagnifierPose = "present";
            }
            else
            {
                // (2026-11-13, the user: "Spark's pose looking at the magnifier is odd") Peering: Spark looks
                // through the lens at a spot on its surface ahead (toward what it looks at), the lens held out
                // between its eyes and that spot.
                MagnifierPose = "peer";
                Vector3 toward = SparkWalking && focus != null ? focus.position - origin : forward;
                Peer(body, origin, forward, right, toward, out grip, out turn, out Vector3 spot);
                if (magnifierLook == null) { magnifierLook = new GameObject("Magnifier Look").transform; magnifierLook.SetParent(transform, false); }
                magnifierLook.position = spot;
                spark.lookTarget = magnifierLook;
                magnifierOwnsLook = true;
            }
            Vector3 wrist=grip-turn*Vector3.Scale(magnifierPalmLocal,hand.lossyScale);
            if(walking) body.Hold(true,new Pose(body.transform.InverseTransformPoint(wrist),Quaternion.Inverse(body.transform.rotation)*turn),body.transform);
            else body.Hold(true,new Pose(wrist,turn));
            var follower=magnifier.GetComponent<NotebookPropFollower>();
            follower.hand=hand; follower.handLocal=magnifierPalmLocal; follower.rotationFrame=walking?body.transform:null;
            follower.rotation=walking?Quaternion.Inverse(body.transform.rotation)*turn:turn; follower.gripLocal=MagnifierHandle;
        }

        // Spark's look while peering through the lens (CrewToolsLate leaves it to the magnifier).
        private Transform magnifierLook;
        private bool magnifierOwnsLook;
        public Vector3 MagnifierSpot => magnifierLook != null ? magnifierLook.position : Vector3.zero;
        public string MagnifierPose { get; private set; } = "";

        // Held up on Spark's right (its magnifier hand), outside its big head, the lens near eye height and its glass
        // toward the person, the handle tipped in to the mitten (which stays within HandReach). The least tip and the
        // highest lens that keep the rim out of the head and, seen from the person's eyes, 1.5 cm off the face.
        private static readonly float[] PresentTilts = { 22f, 30f, 38f, 46f }, PresentDrops = { .004f, .012f, .02f, .028f, .036f };
        private void Present(WindupBody body, Vector3 origin, Vector3 forward, Vector3 right, out Vector3 bestGrip, out Quaternion bestTurn)
        {
            Vector3 eye = Head != null ? Head.position : origin + forward * .6f + Vector3.up * .45f;
            Vector3 face = FaceFront(body, origin, forward);
            Vector3 toPerson = Vector3.ProjectOnPlane(eye - origin, Vector3.up);
            toPerson = toPerson.sqrMagnitude > 1e-6f ? toPerson.normalized : forward;
            float eyeHeight = face.y - origin.y;
            float need = Mathf.Atan2(.015f, Mathf.Max(.05f, Vector3.Distance(eye, face))) * Mathf.Rad2Deg;
            bestGrip = origin + right * .05f + forward * .02f + Vector3.up * .05f;
            bestTurn = Quaternion.LookRotation(toPerson, Vector3.up);
            float bestScore = float.NegativeInfinity;
            foreach (float drop in PresentDrops)
                foreach (float tilt in PresentTilts)
                {
                    // The handle tips in: the lens's top leans out to Spark's right.
                    Quaternion turn = Quaternion.LookRotation(toPerson, Vector3.up) * Quaternion.Euler(0f, 0f, -tilt);
                    Vector3 lensFromGrip = -(turn * MagnifierHandle);
                    float sideways = Mathf.Sqrt(Mathf.Max(0f, (HandReach - .004f) * (HandReach - .004f) - .02f * .02f));
                    Vector3 grip = origin + right * sideways + forward * .02f + Vector3.up * (eyeHeight - drop - lensFromGrip.y);
                    Vector3 lens = grip + lensFromGrip;
                    float clear = body.HeadDepth(lens);
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * Mathf.PI / 4;
                        clear = Mathf.Min(clear, body.HeadDepth(lens + turn * new Vector3(Mathf.Cos(a) * MagnifierRadius, Mathf.Sin(a) * MagnifierRadius, 0)));
                    }
                    if (clear < 1.08f) continue;
                    float gap = Vector3.Angle(lens - eye, face - eye)
                                - Mathf.Atan2(.03f, Mathf.Max(.01f, Vector3.Distance(eye, face))) * Mathf.Rad2Deg
                                - Mathf.Atan2(MagnifierRadius, Mathf.Max(.01f, Vector3.Distance(eye, lens))) * Mathf.Rad2Deg;
                    float score = gap >= need ? 100f - drop * 400f - tilt * .2f : gap;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    bestGrip = grip;
                    bestTurn = turn;
                    MagnifierFaceGap = gap;
                    MagnifierFaceNeeded = need;
                }
        }
        public float MagnifierFaceNeeded { get; private set; } = FaceClearDegrees;

        // The lens on the line from Spark's eyes down to a spot on its surface `PeerDistances` ahead (toward `toward`,
        // at most PeerTurn off its own front), swaying slowly across it. Out of a few spots along that line the highest
        // one is taken that keeps the lens out of the head and, seen from the person's eyes, off the face; the handle
        // then hangs from the lens toward the body, and the mitten holds it within HandReach.
        // (A spot 13-20 cm ahead bowed the big head right down, its top toward the person: now 22-34 cm, a gentler look.)
        private static readonly float[] PeerDistances = { .34f, .28f, .22f }, PeerReach = { .088f, .08f, .072f, .064f };
        public const float PeerTurn = 35f;
        private void Peer(WindupBody body, Vector3 origin, Vector3 forward, Vector3 right, Vector3 toward, out Vector3 bestGrip, out Quaternion bestTurn, out Vector3 bestSpot)
        {
            Vector3 eye = Head != null ? Head.position : origin + forward * .6f + Vector3.up * .45f;
            Vector3 face = FaceFront(body, origin, forward);
            const float faceRadius = .03f;
            float need = MagnifierFaceNeeded = Mathf.Atan2(.015f, Mathf.Max(.05f, Vector3.Distance(eye, face))) * Mathf.Rad2Deg; // 1.5 cm off the face
            Vector3 flat = Vector3.ProjectOnPlane(toward, Vector3.up);
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : forward;
            float off = Vector3.SignedAngle(forward, flat, Vector3.up);
            flat = Quaternion.AngleAxis(Mathf.Clamp(off, -PeerTurn, PeerTurn), Vector3.up) * forward;
            Vector3 across = Vector3.Cross(Vector3.up, flat);
            float sway = Mathf.Sin(Time.time * .9f) * .012f + .006f; // a slow look across the spot, a little toward its right hand
            bestSpot = origin + flat * PeerDistances[0] + across * sway;
            bestGrip = origin + forward * .045f + Vector3.up * .03f;
            bestTurn = Quaternion.LookRotation(forward, Vector3.up);
            float bestScore = float.NegativeInfinity;
            bool any = false;
            foreach (float distance in PeerDistances)
            {
                Vector3 spot = origin + flat * distance + across * sway;
                Vector3 sight = (spot - face).normalized;
                Quaternion turn = Quaternion.LookRotation(sight, Vector3.up);
                foreach (float reach in PeerReach)
                {
                    // The point on the sight line `reach` ahead of the body's axis.
                    float along = Vector3.Dot(sight, flat);
                    if (along < .1f) continue;
                    float k = (reach - Vector3.Dot(face - origin, flat)) / along;
                    if (k <= .01f) continue;
                    Vector3 lens = face + sight * k;
                    Vector3 grip = lens + turn * MagnifierHandle;
                    if (Vector3.ProjectOnPlane(grip - origin, Vector3.up).magnitude > HandReach - .003f || grip.y < origin.y + .012f) continue;
                    float clear = body.HeadDepth(lens);
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * Mathf.PI / 4;
                        clear = Mathf.Min(clear, body.HeadDepth(lens + turn * new Vector3(Mathf.Cos(a) * MagnifierRadius, Mathf.Sin(a) * MagnifierRadius, 0)));
                    }
                    if (clear < 1.12f) continue;
                    float gap = Vector3.Angle(lens - eye, face - eye)
                                - Mathf.Atan2(faceRadius, Mathf.Max(.01f, Vector3.Distance(eye, face))) * Mathf.Rad2Deg
                                - Mathf.Atan2(MagnifierRadius, Mathf.Max(.01f, Vector3.Distance(eye, lens))) * Mathf.Rad2Deg;
                    // Clear of the face: the farthest look, the lens farthest out; else the least covering.
                    float score = gap >= need ? 100f + distance * 100f + reach * 10f : gap;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    bestGrip = grip;
                    bestTurn = turn;
                    bestSpot = spot;
                    MagnifierFaceGap = gap;
                    any = true;
                }
            }
            if (!any) MagnifierFaceGap = -99f;
        }

        // The middle of the face: on the head's front surface at eye height (like WindupsTitle.EyeSpot).
        private static Vector3 FaceFront(WindupBody body, Vector3 origin, Vector3 forward)
        {
            float eyeHeight = Mathf.Max(.07f, body.HeadTop.y - origin.y - .058f);
            Vector3 from = origin + Vector3.up * eyeHeight;
            for (float d = 0f; d < .12f; d += .003f)
            {
                Vector3 p = from + forward * d;
                if (body.HeadDepth(p) >= 1f) return p;
            }
            return from + forward * .05f;
        }
    }
}
