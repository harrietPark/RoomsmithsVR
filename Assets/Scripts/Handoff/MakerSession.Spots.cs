using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F30 C: the idea meeting, in the room.
    //  - It only starts when the person says they can't think of anything ("no idea", "뭐 만들지 모르겠어"),
    //    asks the crew to look around, or stays quiet for a while (StuckSilence) after "what shall we make?".
    //    Nothing else makes the crew pitch ideas on its own.
    //  - Spark looks (one camera picture) and circles the things it sees; the crew also knows the desk's
    //    front edge and a free patch of desk. Each teammate then walks over and sticks one idea on a real spot:
    //    a post-it in its own colour with its own face sticker, two to four words. Spark brings something
    //    fun, Maker something useful, Fixer the fix for an annoyance.
    //  - "Compare them": each stands by the note it prefers (the vote). Pinch a note (near or far): that is
    //    what we make. The others go on the idea wall (with who wrote them) for next time.
    public sealed partial class MakerSession
    {
        public sealed class MeetSpot { public string name, kind; public Vector3 point, outward; public RoomEyes.Found found; }

        // Seconds of quiet after "what shall we make?" before the crew offers to find ideas (tests shorten it).
        public static float StuckSilence = 15f;
        public const float NoteSize = .07f;
        public bool InMeeting { get; private set; }
        public int Meetings { get; private set; }
        public string MeetingTrigger { get; private set; } = "";
        public readonly List<MeetSpot> Spots = new List<MeetSpot>();

        private bool stuckOffered;
        private readonly Dictionary<Crew, Material> faceMaterials = new Dictionary<Crew, Material>();

        // "I don't know what to make" (F45: only points at Find ideas; asking for ideas is MakerSession.AsksForIdeas).
        public static bool StuckWords(string p) => new[]
        {
            "don't know what", "dont know what", "do not know what", "no idea", "not sure", "can't think", "cant think", "cannot think", "no clue",
            "nothing comes", "모르겠", "생각이 안", "생각 안 나", "아이디어가 없", "아이디어 없", "뭐 만들지", "뭘 만들지"
        }.Any(p.Contains);

        public static Color NoteColour(Crew crew) => Color.Lerp(MakerRoles.Color(crew), Color.white, crew == Crew.Fixer ? .62f : .55f);

        // ------------------------------------------------------------ when

        // Called every frame (CrewFrame): quiet after "what shall we make?" -> the crew offers ideas once.
        private void StuckFrame(float quiet)
        {
            if (Current != Step.Suggesting) { stuckOffered = false; return; }
            if (stuckOffered || InMeeting || Ideas.Count > 0 || !string.IsNullOrEmpty(Need) || looking || SparkWalking || crewBusy || Holding ||
                NotebookOpen || CarriedNote != null || lines.Count > 0 || Voice.IsSpeaking || Voice.Loading || userWasTalking) return; // an unanswered "Did it fit?" does not hold it back
            if (quiet < StuckSilence) return;
            stuckOffered = true;
            // F45: the crew never looks around unasked: after a quiet while Maker only reminds of Find ideas, once.
            if (Question == null) AskWith(Crew.Maker, "Tell me what to make, or tap Find ideas!", false, IdeaReplies);
        }

        // F45: at the first note the person pressed Find ideas (or asked out loud; WindupsArrival.BeginQuietMeeting):
        // begin with nothing chosen and go straight to the meeting.
        private bool quietStart;
        public void BeginQuiet(string trigger = "button")
        {
            quietStart = true;
            Begin("");
            quietStart = false;
            stuckOffered = true;
            Body(Crew.Spark)?.Play("HandRaise");
            StartCoroutine(Suggest(trigger));
        }

        // ------------------------------------------------------------ the meeting

        private IEnumerator Suggest(string trigger = "asked")
        {
            if (InMeeting) yield break;
            InMeeting = true;
            stuckOffered = true;
            Meetings++;
            MeetingTrigger = trigger;
            Current = Step.Suggesting;
            ClearIdeas();
            Ack(Crew.Spark, "Ooh, let me look around!");
            List<RoomEyes.Found> seen = null;
            yield return SparkLooks("things on the desk the person might want to make something for, and messy or empty spots", list => seen = list);
            Spots.Clear();
            Spots.AddRange(MeetingSpots(seen));
            var names = (seen ?? new List<RoomEyes.Found>()).Select(f => f.seen.name).ToArray();
            var made = LoadMemory().items.Select(m => m.name).Distinct().ToList();
            string json = null;
            yield return Ask(Crew.Spark, "ideas", new
            {
                seen = names, spots = Spots.Select(s => s.name).ToArray(), made = made.Skip(Mathf.Max(0, made.Count - 6)).ToArray(),
                meshy = MeshyReady, said = Need, talk = Talk.ToArray()
            }, r => json = r);
            IdeasReply reply = null;
            if (json != null)
                try { reply = JsonConvert.DeserializeObject<IdeasReply>(json); }
                catch (Exception exception) { Debug.LogWarning("[Windups Meeting] " + exception.Message); }
            var ideas = (reply?.ideas ?? new MakerIdea[0]).Where(i => i != null && !string.IsNullOrWhiteSpace(i.label)).Take(3).ToList();
            if (ideas.Count == 0 || Current != Step.Suggesting)
            {
                InMeeting = false;
                if (Current == Step.Suggesting) Say(Crew.Maker, "Tell me what you'd like, then!");
                yield break;
            }
            // One note each, in role order when the AI left someone out.
            var free = new List<Crew> { Crew.Spark, Crew.Maker, Crew.Fixer };
            var who = new Crew[ideas.Count];
            for (int i = 0; i < ideas.Count; i++)
                if (Enum.TryParse(ideas[i].who, out Crew c) && free.Contains(c)) { who[i] = c; free.Remove(c); }
                else who[i] = (Crew)(-1);
            for (int i = 0; i < ideas.Count; i++) if ((int)who[i] < 0) { who[i] = free[0]; free.RemoveAt(0); }
            // Spark visits the things the ideas are for; then everyone takes a note to its spot.
            yield return Tour(TourStopsFor(seen, ideas.Select(i => i.forObject)));
            if (Current != Step.Suggesting) { InMeeting = false; yield break; }
            var taken = new List<Vector3>();
            var running = new List<Choice>();
            for (int i = 0; i < ideas.Count; i++)
            {
                var spot = SpotFor(ideas[i]);
                var pose = NotePose(spot, taken);
                taken.Add(pose.position);
                var root = SpotNote(who[i], MakerRoles.Clean(ideas[i].label));
                root.gameObject.SetActive(false);
                var choice = new Choice { root = root, idea = ideas[i], found = spot?.found, width = NoteSize, height = NoteSize, home = pose.position, spot = spot, who = who[i] };
                Ideas.Add(choice);
                running.Add(choice);
                StartCoroutine(BringNote(choice, pose, i * .45f));
            }
            float until = Time.time + 12f;
            while (Time.time < until && running.Any(c => c.root != null && !c.placed)) yield return null;
            InMeeting = false;
            if (Current != Step.Suggesting) yield break;
            Talk.Add("Ideas on the notes: " + string.Join("; ", running.Select(c => MakerRoles.Name(c.who) + ": " + MakerRoles.Clean(c.idea.label) + " (" + c.idea.kind + ")")));
            if (!string.IsNullOrWhiteSpace(reply.say)) Say(Crew.Spark, MakerRoles.Clean(reply.say));
            Say(Crew.Maker, "Pinch the one you like, or ask us to compare.");
        }

        // The things Spark saw, the desk's front edge and a free patch of desk.
        private List<MeetSpot> MeetingSpots(List<RoomEyes.Found> seen)
        {
            var list = new List<MeetSpot>();
            foreach (var f in (seen ?? new List<RoomEyes.Found>()).Where(f => f != null && f.onSurface).Take(4))
                list.Add(new MeetSpot { name = MakerRoles.Clean(f.seen.name), kind = "object", point = f.basePoint, found = f });
            if (ProbeEdge(out var edge, out var outward)) list.Add(new MeetSpot { name = "desk edge", kind = "edge", point = edge, outward = outward });
            if (SurfaceSpot(out var space, out _)) list.Add(new MeetSpot { name = "empty desk space", kind = "space", point = space });
            return list;
        }

        private MeetSpot SpotFor(MakerIdea idea)
        {
            string f = (idea.forObject ?? "").ToLowerInvariant().Trim();
            MeetSpot spot = null;
            if (f.Length > 0)
                spot = Spots.FirstOrDefault(s => s.name.ToLowerInvariant() == f)
                       ?? Spots.FirstOrDefault(s => s.kind == "object" && (s.name.ToLowerInvariant().Contains(f) || f.Contains(s.name.ToLowerInvariant())))
                       ?? (f.Contains("edge") || f.Contains("모서리") ? Spots.FirstOrDefault(s => s.kind == "edge") : null)
                       ?? (f.Contains("space") || f.Contains("empty") || f.Contains("desk") ? Spots.FirstOrDefault(s => s.kind == "space") : null);
            spot ??= idea.kind == "clip" ? Spots.FirstOrDefault(s => s.kind == "edge") : idea.kind == "tray" ? Spots.FirstOrDefault(s => s.kind == "space") : null;
            spot ??= Spots.FirstOrDefault(s => s.kind == "object") ?? Spots.FirstOrDefault();
            return spot ?? new MeetSpot { name = "desk", kind = "space", point = DeskPointInFront() };
        }

        // The note stands on the desk at its spot (in front of a thing, behind the edge), leaning back a
        // little toward the person's eyes; notes never stand on top of each other.
        private Pose NotePose(MeetSpot spot, List<Vector3> taken)
        {
            Vector3 head = Head != null ? Head.position : spot.point + Vector3.up * .5f + Vector3.back * .5f;
            Vector3 toUser = Vector3.ProjectOnPlane(head - spot.point, Vector3.up);
            toUser = toUser.sqrMagnitude > 1e-6f ? toUser.normalized : Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, toUser);
            Vector3 at = spot.point;
            if (spot.kind == "object" && spot.found != null) at += toUser * (Mathf.Clamp(spot.found.width, .02f, .2f) * .5f + .015f);
            else if (spot.kind == "edge") at -= spot.outward * .04f;
            for (int k = 0; k < 6 && taken.Any(t => Vector3.ProjectOnPlane(t - at, Vector3.up).magnitude < NoteSize * 1.25f); k++) at += side * NoteSize * 1.3f * (k % 2 == 0 ? 1f : -2f);
            // (2026-11-12) A note for a spot on the working desk stays on the desk when it moves aside.
            if (OnWorkDesk(spot.point, -.02f)) at = WorkPoint(at, NoteSize * .5f);
            // F51: never on a teammate, a thing made, the mic or another note: the nearest free patch.
            if (KeepClear)
            {
                Vector3 foot = new Vector3(at.x, DeskHeightAt(at, spot.point.y), at.z);
                if (FreeDeskSpot(foot, NoteFoot, null, out var free, taken)) at = new Vector3(free.x, at.y, free.z);
            }
            float desk = DeskHeightAt(at, spot.point.y);
            // F51: a spot on top of something the room model has no surface for (a box, a shelf): the note
            // stands on that top, not on the floor under it (seen in the Editor room: 74 cm below its spot).
            if (spot.point.y - desk > .06f && DeskTop(new Vector3(at.x, spot.point.y, at.z), out float top) && Mathf.Abs(top - spot.point.y) < .05f) desk = top;
            const float lean = 14f;
            float s = Mathf.Sin(lean * Mathf.Deg2Rad), c = Mathf.Cos(lean * Mathf.Deg2Rad);
            Vector3 up = Vector3.up * c - toUser * s;
            Vector3 forward = -toUser * c - Vector3.up * s;
            Vector3 centre = new Vector3(at.x, desk, at.z) + up * (NoteSize * .5f + .002f);
            return new Pose(centre, Quaternion.LookRotation(forward, up));
        }

        // The teammate walks toward its spot (on its own surface, a short way), then the note flies from its
        // hands and sticks; it walks back to its seat.
        private IEnumerator BringNote(Choice choice, Pose pose, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (choice.root == null) yield break;
            bool walked = false;
            Vector3 home = Vector3.zero;
            RoomCapsule.CapsulePetController pet = null;
            Vector3 from = pose.position + Vector3.up * .1f;
            if (cast.TryGetValue(choice.who, out var character) && character != null)
            {
                pet = character.movement;
                home = pet.transform.position;
                if (choice.who != Crew.Spark && WalkableNear(pet, pose.position, out var goal))
                {
                    pet.UnlockMeetingSeat();
                    yield return null;
                    walked = pet.GoTo(goal, 2.5f);
                    float until = Time.time + 4.5f;
                    while (walked && Time.time < until && pet.State == RoomCapsule.CapsulePetController.MotionState.Walking && Vector3.Distance(pet.transform.position, goal) > .05f) yield return null;
                }
                focus.position = pose.position;
                character.lookTarget = focus;
                Body(choice.who)?.Play("HandRaise");
                from = character.transform.position + Vector3.up * .12f;
            }
            if (choice.root == null) yield break;
            choice.root.gameObject.SetActive(true);
            choice.root.SetPositionAndRotation(from, pose.rotation);
            ToySounds.Play(ToySounds.Cue.Paper, from, .45f, 1.3f);
            for (float t = 0f; t < 1f && choice.root != null; t += Time.deltaTime / .5f)
            {
                float e = Mathf.SmoothStep(0f, 1f, t);
                choice.root.position = Vector3.Lerp(from, pose.position, e) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .06f;
                choice.root.localScale = Vector3.one * Mathf.Lerp(.4f, 1f, e);
                yield return null;
            }
            if (choice.root == null) yield break;
            choice.root.SetPositionAndRotation(pose.position, pose.rotation);
            choice.root.localScale = Vector3.one;
            choice.placed = true;
            ToySounds.Play(ToySounds.Cue.Press, pose.position, .5f, 1.7f);
            ToyFx.Puff(pose.position, .008f, 3);
            if (character != null) character.lookTarget = null;
            if (!walked || pet == null) yield break;
            yield return new WaitForSeconds(.4f);
            pet.GoTo(ClearSeat(choice.who, home), 3f); // F51: its seat may have a note on it now
            float back = Time.time + 6f;
            yield return new WaitForSeconds(.2f);
            while (Time.time < back && pet.State == RoomCapsule.CapsulePetController.MotionState.Walking) yield return null;
            for (int i = 0; i < 60 && !pet.MeetingSeatLocked; i++) { pet.LockMeetingSeat(); yield return null; }
        }

        // ------------------------------------------------------------ the notes

        // A post-it in the teammate's colour, with its face sticker in the corner and the idea in a few words.
        private Transform SpotNote(Crew crew, string label)
        {
            var root = new GameObject("Idea Note " + crew).transform;
            root.SetParent(transform, false);
            string variant = WindupsPostIt.Variants[(int)crew % 3];
            NoteSheet(root, crew, NoteSize, variant);
            var face = FaceSticker(crew);
            face.SetParent(root, false);
            face.localPosition = StickerSpot(variant, NoteSize);
            face.localRotation = Quaternion.Euler(0f, 0f, crew == Crew.Fixer ? -8f : 10f);
            var canvas = new GameObject("Words", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(root, false);
            rect.sizeDelta = new Vector2(NoteSize * 1000f, NoteSize * 1000f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, -.003f, -.004f);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            const float wordsTop = 20f; // mm below the top of the canvas (whose middle is 3 mm below the note's)
            float wordsHeight = WordsAboveSticker(face.localPosition, NoteSize * 500f - 3f - wordsTop, NoteSize * 1000f - 26f);
            var words = HatIdeaCard.Label(rect, font, 5f, wordsTop, NoteSize * 1000f - 10f, wordsHeight, 6f, 12f,
                Color.Lerp(MakerRoles.Color(crew), Color.black, .65f), FontStyles.Bold, TextAlignmentOptions.Center);
            words.text = label;
            return root;
        }

        // Where the face sticker sits on each designed sheet, so the paper never covers it (2026-11-12). The
        // sheets curl toward the reader: A lifts its top-left corner (5 mm in the model), B rolls its whole top
        // edge (6 mm, 12 mm on a 7 cm note) and C bends both sides (3 mm), so B's sticker goes bottom-left and
        // C's top-middle. `at` is a fraction of the sheet as the reader sees it; `lift` is how high the paper
        // is under the whole sticker, in model mm (measured from the meshes in the Editor; they are not
        // readable on the headset). VerifyRoomFit.RunStickers re-measures it.
        public const float StickerRadius = .0118f;
        public static readonly (string variant, Vector2 at, float lift)[] StickerSpots =
        {
            ("A", new Vector2(.33f, .33f), .35f),
            ("B", new Vector2(-.31f, -.31f), .35f),
            ("C", new Vector2(0f, .33f), .75f),
        };

        public static Vector3 StickerSpot(string variant, float size)
        {
            var spot = StickerSpots.FirstOrDefault(s => s.variant == variant);
            if (spot.variant == null) spot = StickerSpots[0];
            float paper = spot.lift * .001f * Mathf.Min(2f, size / WindupsPostIt.ModelSize); // as NoteSheet/WallView scale it
            return new Vector3(spot.at.x * size, spot.at.y * size, -Mathf.Max(.0035f, paper + .0015f));
        }

        // The words end 1 mm above a sticker in the lower half of the note (B's). `wordsTopMm`: the note's y of
        // the words' top edge, in mm.
        private static float WordsAboveSticker(Vector3 sticker, float wordsTopMm, float height) =>
            sticker.y >= 0f ? height : Mathf.Max(10f, Mathf.Min(height, wordsTopMm - (sticker.y + StickerRadius) * 1000f - 1f));

        // The designed post-it sheet, tinted.
        private void NoteSheet(Transform root, Crew crew, float size, string variant)
        {
            var model = Resources.Load<GameObject>("WindupsPostIt_" + (string.IsNullOrEmpty(variant) ? "A" : variant));
            if (model == null)
            {
                Part(root, "Sheet", StickerMesh("noteSlab" + crew, () => ToyMeshes.RoundedSlab(size, size, .002f, .001f, NoteColour(crew), Color.Lerp(NoteColour(crew), Color.black, .1f), NoteColour(crew), "NoteSlab")), Vector3.zero, 0f);
                return;
            }
            var look = Instantiate(model, root, false).transform;
            look.name = "Sheet";
            look.localRotation = Quaternion.Euler(-90f, 0f, 0f); // the sheet's face (+Y) toward the reader (-Z)
            float s = size / WindupsPostIt.ModelSize;
            look.localScale = new Vector3(s, Mathf.Min(2f, s), s);
            var block = new MaterialPropertyBlock();
            foreach (var r in look.GetComponentsInChildren<Renderer>(true))
            {
                r.GetPropertyBlock(block);
                block.SetColor("_BaseColor", NoteColour(crew));
                r.SetPropertyBlock(block);
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        // A round sticker: one cell of the teammate's own face sheet (the happy face), with a white rim.
        private Transform FaceSticker(Crew crew)
        {
            var root = new GameObject("Face Sticker " + crew).transform;
            root.SetParent(transform, false);
            Part(root, "Rim", StickerMesh("faceRim", () => ToyMeshes.Disc(StickerRadius, .0012f, Color.white, new Color(.88f, .88f, .9f), "FaceRim")), Vector3.zero, 0f);
            var faceMaterial = FaceMaterial(crew);
            var go = new GameObject("Face", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(0f, 0f, -.0013f);
            go.GetComponent<MeshFilter>().sharedMesh = StickerMesh("faceDisc", () => FaceDisc(WindupBody.Happy, .0102f));
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = faceMaterial != null ? faceMaterial : material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (faceMaterial == null)
            {
                var block = new MaterialPropertyBlock();
                block.SetColor("_BaseColor", MakerRoles.Color(crew));
                renderer.SetPropertyBlock(block);
            }
            return root;
        }

        // The character's own face sheet (3 x 3 faces), as a sticker material.
        private Material FaceMaterial(Crew crew)
        {
            if (faceMaterials.TryGetValue(crew, out var m) && m != null) return m;
            var body = Body(crew);
            var source = body != null && body.head != null ? body.head.sharedMaterial : null;
            if (source == null) return null;
            m = new Material(source);
            if (m.HasProperty("_BaseMap")) { m.SetTextureScale("_BaseMap", Vector2.one); m.SetTextureOffset("_BaseMap", Vector2.zero); }
            m.mainTextureScale = Vector2.one;
            m.mainTextureOffset = Vector2.zero;
            owned.Add(m);
            faceMaterials[crew] = m;
            return m;
        }

        // A flat disc facing the reader (-Z) whose texture is one face cell of the sheet (row 0 at the top).
        private static Mesh FaceDisc(int cell, float radius)
        {
            const int around = 40;
            int col = cell % 3, row = cell / 3;
            var centreUv = new Vector2((col + .5f) / 3f, 1f - (row + .5f) / 3f);
            float uvRadius = .32f / 3f;
            var v = new List<Vector3> { Vector3.zero };
            var uv = new List<Vector2> { centreUv };
            for (int k = 0; k < around; k++)
            {
                float a = k * Mathf.PI * 2f / around;
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                v.Add(new Vector3(d.x, d.y, 0f) * radius);
                uv.Add(centreUv + d * uvRadius);
            }
            var t = new List<int>();
            for (int k = 0; k < around; k++) { t.Add(0); t.Add(1 + (k + 1) % around); t.Add(1 + k); }
            var mesh = new Mesh { name = "FaceDisc" };
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(t, 0);
            mesh.SetNormals(Enumerable.Repeat(Vector3.back, v.Count).ToList());
            mesh.SetColors(Enumerable.Repeat(Color.white, v.Count).ToList());
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------ after the pick

        // The notes not picked leave the desk. F79 (Quest 2026-11-14: "only my note should fly to the wall; the ideas they
        // suggested can just go"): they are not kept on the idea wall any more.
        private void SendOthersToWall(Choice picked)
        {
            foreach (var other in Ideas.Where(c => c != picked && c.root != null))
            {
                StartCoroutine(FlyAway(other.root));
                other.root = null;
            }
        }

        private IEnumerator FlyAway(Transform note)
        {
            if (note == null) yield break;
            Vector3 from = note.position;
            Vector3 to = from + Vector3.up * .25f;
            for (float t = 0f; t < 1f && note != null; t += Time.deltaTime / .5f)
            {
                note.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t));
                note.localScale = Vector3.one * Mathf.Lerp(1f, .2f, t);
                yield return null;
            }
            if (note == null) yield break;
            ToyFx.Puff(note.position, .008f, 3);
            Destroy(note.gameObject);
        }

        // A wall note written by a teammate wears its colour and face (on its sheet's flat spot, the words above it).
        private void DressWallNote(Transform root, WallNote note, TextMeshProUGUI words, float wordsTopMm)
        {
            if (root == null || note == null || string.IsNullOrEmpty(note.who)) return;
            int index = Array.IndexOf(MakerRoles.Names, note.who);
            if (index < 0) return;
            var crew = (Crew)index;
            var sheet = root.Find("Sheet");
            if (sheet != null)
            {
                var block = new MaterialPropertyBlock();
                foreach (var r in sheet.GetComponentsInChildren<Renderer>(true))
                {
                    r.GetPropertyBlock(block);
                    block.SetColor("_BaseColor", NoteColour(crew));
                    r.SetPropertyBlock(block);
                }
            }
            var face = FaceSticker(crew);
            face.SetParent(root, false);
            face.localPosition = StickerSpot(string.IsNullOrEmpty(note.variant) ? "A" : note.variant, WallSheet);
            face.localRotation = Quaternion.Euler(0f, 0f, 9f);
            if (words != null)
            {
                var size = words.rectTransform.sizeDelta;
                words.rectTransform.sizeDelta = new Vector2(size.x, WordsAboveSticker(face.localPosition, wordsTopMm, size.y));
            }
        }

        // ------------------------------------------------------------ test entries

        public void SimulateQuiet(float seconds) => lastHeardAt = lastLineAt = lastStepAt = Time.time - seconds;
    }
}
