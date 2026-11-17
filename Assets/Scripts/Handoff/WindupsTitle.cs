using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using Windups.BubbleLab;

namespace Windups.Handoff
{
    // F28 launch intro for Thinkmade (v4). After the room is ready, a toy title plate pops up in front
    // of the person (world-locked, ~1 m, a little below the eyes). "Thinkmade" bounces in letter by
    // letter and the wind-up key on its side clicks.
    // F35 (2026-11-12, chosen design "B: the title is a stage"): the crew used to be lifted straight up
    // behind the floating plate (feet hanging in the air below it) and greeted standing on nothing. Now
    // the plate floats a little above the surface in front of the person (lowered toward it, at most
    // StageLowest below the eyes), Spark, Maker and Fixer wait ON that surface under/behind it, hop up one
    // after another and land standing on the plate's top (squash, "tok"), greet there each in its own way
    // (Maker with a "..." thought, then its hello), and hop down one after another onto their seats (physics
    // landing) while the plate folds away. With no surface within StageRiseMax below the plate, a
    // character hops up from just behind the plate instead (hidden until then). Feet are always on something.
    // A pinch skips ahead; later launches play a short version.
    // F52 (2026-11-12, the user: "the title should pop up, and the crew should go straight up at the same time;
    // now they wait below for a while"): the plate springs up out of the surface (rising, stretching, then
    // settling with a squash) and in that same moment the crew jumps onto it, one right after another
    // (EntryStagger); the letters bounce in and the key spins while they fly. On the stage each one shows its
    // own tool instead of a plain wave (WindupsTitle.Tools.cs).
    // 2026-11-13 (the user's pick, after rendered drafts): the app is Roomsmiths and its title is a block sign - a
    // light wooden tray with ten cream letter blocks wearing pastel caps, "Roomsmiths" in black (Fredoka), a small
    // brass wind-up key on the tray's right end. The tray springs up and the blocks pop up out of it one by one
    // (instead of letters bouncing in); Spark stands on the o, Maker between s and m, Fixer on the h.
    // 2026-11-13 later (the user: "the cubes look like keyboard keycaps"; picked draft A "wooden toy blocks", then
    // "the wood colours should differ more, and give every object the characters' shader"): no caps - each letter is
    // a wooden block rounded on every edge (ToyMeshes.RoundedBox), each in its own wood (birch to walnut), with a
    // pastel rounded panel on its face carrying the black letter; the blocks stand turned a few degrees and a few at
    // a hair's height (never the ones the crew stands on), so the row stops reading as a keyboard. Everything the
    // title builds (tray, blocks, key, Maker's thought, the intro tools) renders with the crew's own shader
    // (Resources/ToyCharacterProps: Windups/Toy Character with vertex colours read as sRGB).
    public sealed partial class WindupsTitle : MonoBehaviour
    {
        public const float EntryStagger = .08f, PopSeconds = .42f, PopRise = .07f, LettersDelay = .16f;
        public readonly List<float> TakeoffTimes = new List<float>();
        public readonly List<string> GreetingLog = new List<string>();
        public const string SeenKey = "thinkmade.introSeen";
        // 2026-11-13 (the user after a Quest try: "in the title the characters and the things overlap"): the crew
        // stood 11.5 cm apart (3.5 cm between bodies) and every tool came out on the inner side, into a neighbour.
        // The plate is wider and they stand 14 cm apart; tools come out on each one's outer side (Tools.cs).
        public const string Word = "Roomsmiths";
        public const int Letters = 10;
        public const float BlockSize = .06f, BlockGap = .005f, TrayThick = .028f, BlockRound = .17f; // BlockRound: edge radius / block size
        public const float BlockLead = .05f, BlockStep = .03f, BlockPop = .22f; // seconds after the tray pops: first block, each next, one pop
        // F67 (the user: "Room, then a line break, then smiths"): two lines of stacked blocks - "smiths" (6) on the tray,
        // "Room" (4) standing on its middle four. "smiths" pops up first, "Room" a beat (WordPause) after, onto it.
        public const int WordBreak = 4; // the first block of "smiths"
        public const float WordPause = .12f;
        private const float Pitch = BlockSize + BlockGap;
        // The whole sign as one box (for placing it): the tray's width (six blocks), the tray and two blocks high, the tray deep.
        public const float PlateWidth = (Letters - WordBreak) * Pitch - BlockGap + .06f, PlateHeight = TrayThick + 2f * BlockSize, PlateDepth = .1f;
        // The stage: the plate's bottom is wanted StageGap above the surface the crew waits on, but the plate
        // never goes more than StageLowest below the eyes (it must stay easy to read); a hop up is at most
        // StageRiseMax; the crew stands StageSpacing apart on the plate and waits StageBehind behind its face.
        // 2026-11-16 (the user: a little higher): the sign starts BelowEyes under the eyes (was .13) and is lowered
        // toward the desk only to StageGap above it (was .28), at most StageLowest under the eyes (was .45); the lowest
        // place that is in full view is then raised StageLift where it stays in view.
        public const float BelowEyes = .07f, StageLift = .06f;
        public const float StageGap = .38f, StageLowest = .33f, StageRiseMax = .85f, StageSpacing = .14f, StageBehind = .15f;
        public static readonly Color Ink = new Color(.255f, .267f, .294f), Coral = new Color(.941f, .451f, .376f),
            Paper = Color.white, Tan = new Color(.902f, .855f, .769f), Gold = new Color(1f, .77f, .3f);
        // The block sign's colours (sRGB; the props material reads vertex colours as sRGB). A pale tray so the woods stand out.
        public static readonly Color WoodTop = new Color(.937f, .859f, .722f), WoodEdge = new Color(.867f, .749f, .573f), LetterInk = new Color(.11f, .11f, .125f);
        static readonly Color Birch = new Color(.922f, .827f, .651f), Honey = new Color(.875f, .682f, .431f), Oak = new Color(.784f, .573f, .353f),
            Cherry = new Color(.761f, .486f, .333f), Walnut = new Color(.604f, .408f, .271f), Maple = new Color(.941f, .804f, .58f),
            SideWood = new Color(.725f, .537f, .314f);
        // Each block's wood (no two neighbours alike) and its face panel's pastel (lightened half way to white).
        static readonly Color[] Woods = { Birch, Honey, Walnut, Maple, Oak, Cherry, Birch, Walnut, Maple, Honey };
        static readonly Color[] Pastels =
        {
            new Color(1f, .847f, .42f), new Color(.498f, .827f, .627f), new Color(.553f, .722f, .949f), new Color(1f, .663f, .541f), new Color(.725f, .639f, .933f),
            new Color(.498f, .827f, .627f), new Color(1f, .847f, .42f), new Color(.553f, .722f, .949f), new Color(1f, .663f, .541f), new Color(.725f, .639f, .933f)
        };
        // A few degrees of turn (about the vertical, so tops stay level) so the rows are not a keyboard. (F67: no extra
        // height any more - "Room" stands on "smiths", and the crew stands on blocks of both.)
        static readonly float[] BlockTurn = { -5f, 2f, -3f, 4f, -1f, 1f, -4f, 3f, 0f, -3f };
        static readonly float[] BlockLift = new float[Letters];

        public WindupsArrival arrival;
        // F78 airplane mode (no internet, no room scan): the sign stands PlateAhead in front of the eyes and
        // PlateBelowEyes below them (there is no surface to put it on). After the hello the crew hops down as usual, but
        // with no floor they land in the air (AirSeat: where the tray was, a little wider apart) and stay there, and the
        // sign folds away as in the app (the user: "the logo can go, like the app"; "they jump, and if it's not the
        // floor they just stay in the air"). A teammate picked up and let go stays in the air where it was let go.
        public bool PlateMode;
        public const float PlateAhead = .48f, PlateBelowEyes = .40f, AirSpread = 1.25f;
        public int HeldInAir { get; private set; }  // (checks) a teammate caught falling and kept where it was
        public string Stage { get; private set; } = "";
        public bool Done { get; private set; }
        public bool Skipping { get; private set; }
        public bool ShortVersion { get; private set; }
        public Transform Root { get; private set; }
        public Transform Plate => plate;
        public TextMeshPro Wordmark => word;
        public float HoppedAt { get; private set; } = -1f;
        public int Landed { get; private set; }
        public int OnStage { get; private set; }            // characters standing on the plate's top
        public int WaitedOnSurface { get; private set; }    // characters that waited on a real surface (not behind the plate)
        public float Lowered { get; private set; }          // how far the plate came down toward the surface (m)
        public readonly HashSet<WindupCharacter> Airborne = new HashSet<WindupCharacter>(); // mid-hop onto the plate
        public Vector3 PlateTop(int index) => StagePoint(index);
        public int BlocksUp { get { int n = 0; foreach (var b in blocks) if (b != null && b.localScale.x > .98f) n++; return n; } } // (checks)

        private Transform plate, key, bubble, stageLook;
        private readonly List<Transform> blocks = new List<Transform>();
        private readonly List<Vector3> blockRest = new List<Vector3>();
        private readonly Dictionary<WindupCharacter, Transform> lookBefore = new Dictionary<WindupCharacter, Transform>();
        private TextMeshPro word;
        private Material material;
        private readonly List<Object> owned = new List<Object>();
        private float lettersFrom = -1f, plateShownAt = -1f, plateGoneAt = -1f, bubbleAt = -1f;
        private float scale = 1f;
        private bool lettersSettled;
        private readonly Dictionary<Hand, bool> wasPinching = new Dictionary<Hand, bool>();

        public void Skip() => Skipping = true;
        public float SkippedAt { get; private set; } = -1f;

        // Editor tests: skip as soon as a stage starts (SessionState "Thinkmade.SkipAt").
        private void Mark(string stage)
        {
            Stage = stage;
#if UNITY_EDITOR
            if (!Skipping && UnityEditor.SessionState.GetString("Thinkmade.SkipAt", "") == stage) { Skip(); SkippedAt = Time.time; }
#endif
        }

        // The whole intro; returns when the crew has jumped off and the plate is gone.
        public IEnumerator Play()
        {
            ShortVersion = PlayerPrefs.GetInt(SeenKey, 0) == 1;
            scale = ShortVersion ? .55f : 1f;
            var head = arrival.handoff.meeting.Head;
            var crew = Crew();
            material = PropsMaterial();
            // F79 (2026-11-14, the user: the crew's jump onto the title stutters): the room's navigation rebuild
            // (EffectMesh, a synchronous NavMesh bake and the furniture links, all in one frame) ran right at the hop
            // when the room changed just after loading. Wait until the room has stayed built for RoomSteady.
            yield return RoomSteadyFirst();
            Place(head);
            // Built over several frames (one block a frame, each letter's text made then) and drawn once at a size
            // nobody can see, so neither the meshes nor the first draw of their materials land on the pop.
            yield return Build();
            var waits = WaitingSpots(crew.Count);

            // 1. (F52) The plate springs up out of the surface and in the same moment the crew jumps straight
            //    up onto it, one right after another; the letters bounce in and the key spins while they fly.
            Mark("plate");
            plateShownAt = Time.time;
            ToySounds.Play(ToySounds.Cue.Pop, Root.position, .8f, .9f);
            ToyFx.Puff(Root.position - Root.up * PopRise, .02f, 6);
            for (int i = 0; i < crew.Count; i++)
            {
                lookBefore[crew[i]] = crew[i].lookTarget;
                WindupBody.Of(crew[i])?.BeginIntro();
                crew[i].lookTarget = stageLook;
                if (!waits[i].HasValue) continue;
                WaitedOnSurface++;
                crew[i].movement.Present(waits[i].Value, FacingFrom(waits[i].Value));
            }
            // 2026-11-15 (the user: "when the title appears, a wind-up key winding with its sound, and a sound for the
            // title"; then, after the Quest try: "the characters wait under the title and then go up - do it as before"):
            // the blocks spring up with the plate to a music-box run and the crew jumps on at once (F52); meanwhile the
            // brass key winds in WindClicks ratchet clicks (the tray knocks with each) and then lets go with a whirr.
            blocksFrom = Time.time;
            if (!Skipping) ToySounds.Play(ToySounds.Cue.Jingle, Root.position, .75f, 1f);
            StartCoroutine(WindAndLetGo());
            Mark("hop");
            HoppedAt = Time.time;
            lettersFrom = Time.time + LettersDelay * scale;
            for (int i = 0; i < Letters; i++) StartCoroutine(LetterSound(i));
            for (int i = 0; i < crew.Count; i++)
            {
                StartCoroutine(HopUp(crew[i], i, waits[i]));
                yield return Wait(EntryStagger);
            }
            for (float stageBy = Time.time + 3f; OnStage < crew.Count && !Skipping && Time.time < stageBy; ) yield return null;
            yield return Wait(.15f);

            // 2. Up there each shows its own tool, a moment apart (Spark's magnifier, Maker's brush, Fixer's
            //    tape measure); Maker thinks "..." and says hello (first launch only).
            Mark("hello");
            for (int i = 0; i < crew.Count; i++)
            {
                crew[i].lookTarget = head;
                StartCoroutine(ShowTool(crew[i], i * ToolStagger * scale, StagePoint(i) - StagePoint(crew.Count / 2), FacingFrom(StagePoint(i))));
            }
            yield return Wait(.35f);
            bubbleAt = Time.time;
            bubble.gameObject.SetActive(true);
            ToySounds.Play(ToySounds.Cue.Pop, bubble.position, .5f, 1.4f);
            if (!ShortVersion)
            {
                yield return Wait(.7f);
                bubble.gameObject.SetActive(false);
                ToyFx.Puff(bubble.position, .008f, 3);
                ShowCaption("Hi! We make things with you.");
                yield return Wait(1.9f);
            }
            else yield return Wait(2.2f);
            ClearTools();
            if (PlateMode)
            {
                // F78: one after another they hop down into the air where the tray was, and the sign folds away.
                Mark("jump");
                bubble.gameObject.SetActive(false);
                stayCrew.Clear();
                stayCrew.AddRange(crew);
                for (int i = 0; i < crew.Count; i++)
                {
                    var pet = crew[i].movement;
                    if (!pet.Presented && !pet.IsHeld) pet.Present(StagePoint(i), FacingFrom(StagePoint(i))); // skipped before its hop
                    crew[i].lookTarget = lookBefore.TryGetValue(crew[i], out var before) ? before : null;
                    StartCoroutine(HopToAir(crew[i], AirSeat(i)));
                    yield return Wait(.3f);
                }
                yield return Wait(.25f);
                plateGoneAt = Time.time;
                ToySounds.Play(ToySounds.Cue.Whoosh, Root.position, .5f, 1.2f);
                yield return new WaitForSeconds(.35f);
                ToyFx.Puff(Root.position, .02f, 6);
                Root.gameObject.SetActive(false);
                for (float airUntil = Time.time + 2f; Time.time < airUntil && Airborne.Count > 0; ) yield return null;
                Landed = crew.Count;
                PlayerPrefs.SetInt(SeenKey, 1);
                PlayerPrefs.Save();
                Stage = "done";
                Done = true;
                yield break;
            }

            // 3. One after another they hop down onto their seats on the real desk (or floor); the plate
            //    folds away.
            Mark("jump");
            bubble.gameObject.SetActive(false);
            var targets = Landings(crew.Count, head);
            for (int i = 0; i < crew.Count; i++)
            {
                var pet = crew[i].movement;
                if (!pet.Presented && !pet.IsHeld) pet.Present(StagePoint(i), FacingFrom(StagePoint(i))); // skipped before its hop
                crew[i].lookTarget = lookBefore.TryGetValue(crew[i], out var before) ? before : null;
                StartCoroutine(HopDown(crew[i], targets[i]));
                yield return Wait(.3f);
            }
            yield return Wait(.25f);
            plateGoneAt = Time.time;
            ToySounds.Play(ToySounds.Cue.Whoosh, Root.position, .5f, 1.2f);
            yield return new WaitForSeconds(.35f);
            ToyFx.Puff(Root.position, .02f, 6);
            Root.gameObject.SetActive(false);
            float until = Time.time + 3f;
            while (Time.time < until && (HoppedDown < crew.Count || crew.Exists(c => c.movement.State == RoomCapsule.CapsulePetController.MotionState.Falling))) yield return null;
            Landed = crew.FindAll(c => c.movement.State != RoomCapsule.CapsulePetController.MotionState.Falling).Count;
            PlayerPrefs.SetInt(SeenKey, 1);
            PlayerPrefs.Save();
            Stage = "done";
            Done = true;
        }

        // Spark, Maker, Fixer left to right (the logo's order).
        private List<WindupCharacter> Crew()
        {
            var list = new List<WindupCharacter>();
            foreach (var name in new[] { "Creativity", "Decisions", "Caution" })
                foreach (var dock in arrival.handoff.docks)
                    if (dock.character != null && dock.character.characterName == name) list.Add(dock.character);
            foreach (var dock in arrival.handoff.docks)
                if (dock.character != null && !list.Contains(dock.character)) list.Add(dock.character);
            return list;
        }

        // About a metre ahead (closer if a wall or furniture is in the way), a little below the eyes,
        // facing the person. World-locked: it never follows the head.
        private void Place(Transform head)
        {
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            forward = forward.sqrMagnitude > .001f ? forward.normalized : Vector3.forward;
            float distance = 1.05f;
            var room = arrival.handoff.meeting.environment;
            if (Physics.Raycast(head.position, forward, out var hit, 1.6f, room.GeometryMask, QueryTriggerInteraction.Ignore))
                distance = Mathf.Clamp(hit.distance - .3f, .65f, 1.05f);
            Vector3 at = head.position + forward * distance - Vector3.up * BelowEyes;
            if (PlateMode)
            {
                // F78: no room to stand it on; within reach, low enough that the crew's heads stay under the notes.
                at = head.position + forward * PlateAhead - Vector3.up * PlateBelowEyes;
                Root = new GameObject("Roomsmiths Title").transform;
                Root.SetParent(transform, false);
                Root.SetPositionAndRotation(at, Quaternion.LookRotation(Vector3.ProjectOnPlane(at - head.position, Vector3.up).normalized, Vector3.up));
                return;
            }
            // F35: the plate is the crew's stage. Nearest the old place first (1.05 m, then closer), it goes where
            // a real surface (the desk) lies behind and below it within a hop, coming down toward that surface
            // (its bottom StageGap above it, never more than StageLowest below the eyes) - but only as far as it
            // stays in full view: never behind furniture (seen from the eyes) and never inside it. With no such
            // place it stays where it was (the crew then hops up from behind it).
            for (float d = distance; d >= .6f - 1e-3f; d -= .1f)
            {
                Vector3 probe = head.position + forward * d - Vector3.up * BelowEyes;
                if (!StageOver(head.position, probe, forward, room, out float y)) continue;
                Lowered = probe.y - y;
                at = new Vector3(probe.x, y, probe.z);
                StageFound = true;
                break;
            }
            Root = new GameObject("Roomsmiths Title").transform;
            Root.SetParent(transform, false);
            Root.SetPositionAndRotation(at, Quaternion.LookRotation(at - head.position, Vector3.up));
        }

        public bool StageFound { get; private set; }

        // A plate at `at` (or lower, down to the wanted height) with walkable surface StageBehind behind it, its top a
        // hop above that surface, and in full view. `y`: the plate centre's height to use.
        private bool StageOver(Vector3 eyes, Vector3 at, Vector3 forward, RoomCapsule.CapsuleRoomEnvironment room, out float y)
        {
            y = at.y;
            Vector3 behind = at + forward * StageBehind - Vector3.up * PlateHeight * .5f;
            if (!Physics.Raycast(behind, Vector3.down, out var hit, StageLowest + StageRiseMax, room.GeometryMask, QueryTriggerInteraction.Ignore) || hit.normal.y < .9f) return false;
            if (!room.TrySameLevelNavPoint(hit.point, out _)) return false;
            float want = Mathf.Max(hit.point.y + StageGap + PlateHeight * .5f, eyes.y - StageLowest);
            for (float h = Mathf.Min(want, at.y); h <= at.y + 1e-4f; h += .03f)
            {
                if (h + PlateHeight * .5f - hit.point.y > StageRiseMax) break;
                if (!PlateInView(eyes, new Vector3(at.x, h, at.z), room.GeometryMask)) continue;
                y = h;
                // then StageLift higher where that is still in view and a hop (never above the start, BelowEyes)
                float up = Mathf.Min(at.y, h + StageLift);
                if (up > h && up + PlateHeight * .5f - hit.point.y <= StageRiseMax && PlateInView(eyes, new Vector3(at.x, up, at.z), room.GeometryMask)) y = up;
                return true;
            }
            return false;
        }

        // The plate (facing the eyes at `centre`) touches no furniture, and its middle, corners and the stage on its
        // top (with a character's height above it) can all be seen from the eyes.
        public static bool PlateInView(Vector3 eyes, Vector3 centre, int mask)
        {
            var turn = Quaternion.LookRotation(Vector3.ProjectOnPlane(centre - eyes, Vector3.up).normalized, Vector3.up);
            if (Physics.CheckBox(centre + turn * new Vector3(0f, .04f, PlateDepth * .5f), new Vector3(PlateWidth * .5f + .01f, PlateHeight * .5f + .05f, PlateDepth * .5f + .01f), turn, mask, QueryTriggerInteraction.Ignore))
                return false;
            float hx = PlateWidth * .45f, hy = PlateHeight * .5f;
            foreach (var local in new[] { Vector3.zero, new Vector3(-hx, -hy, 0f), new Vector3(hx, -hy, 0f), new Vector3(-hx, hy, 0f), new Vector3(hx, hy, 0f), new Vector3(0f, hy + .1f, PlateDepth * .5f) })
            {
                Vector3 p = centre + turn * local;
                Vector3 d = p - eyes;
                if (Physics.Raycast(eyes, d.normalized, d.magnitude - .01f, mask, QueryTriggerInteraction.Ignore)) return false;
            }
            return true;
        }

        public const float RoomSteady = .6f, RoomSteadyLongest = 6f, WarmScale = .002f;
        public int BuildFrames { get; private set; } // (checks)
        public float RoomWaited { get; private set; } // (checks)

        private IEnumerator RoomSteadyFirst()
        {
            var room = arrival.handoff.meeting.environment;
            if (room != null && room.Roomless) { RoomWaited = 0f; yield break; } // F78: no room to wait for
            float from = Time.unscaledTime, steadySince = from;
            while (Time.unscaledTime - from < RoomSteadyLongest)
            {
                if (room == null || !room.IsReady) steadySince = Time.unscaledTime;
                else if (Time.unscaledTime - steadySince >= RoomSteady) break;
                yield return null;
            }
            RoomWaited = Time.unscaledTime - from;
        }

        private IEnumerator Build()
        {
            BuildFrames = 0;
            // The sign: a light wooden tray (a low lip front and back) holding ten letter blocks with pastel caps.
            plate = new GameObject("Block Sign").transform;
            plate.SetParent(Root, false);
            float trayTop = -PlateHeight * .5f + TrayThick;
            var tray = ToyMeshes.RoundedSlab(PlateWidth, PlateDepth, .035f, TrayThick, WoodTop, WoodEdge, WoodEdge, "SignTray");
            var lip = ToyMeshes.RoundedSlab(PlateWidth - .03f, .009f, .0045f, .008f, WoodEdge, WoodEdge, WoodEdge, "SignTrayLip");
            owned.Add(tray); owned.Add(lip);
            var trayPart = Part("Tray", tray, plate);
            trayPart.localPosition = new Vector3(0f, trayTop, PlateDepth * .5f);
            trayPart.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Part("Lip Front", lip, plate).localPosition = new Vector3(0f, trayTop + .0045f, .001f);
            Part("Lip Back", lip, plate).localPosition = new Vector3(0f, trayTop + .0045f, PlateDepth - .009f);
            building = true;
            plate.localScale = Vector3.zero;
            yield return null; BuildFrames++;
            var font = Resources.Load<TMP_FontAsset>("Fredoka SDF");
            if (font == null) font = Resources.Load<TMP_FontAsset>("LilitaOne SDF");
            blocks.Clear();
            blockRest.Clear();
            for (int i = 0; i < Letters; i++)
            {
                // The block's centre; it pops up (scales) about it.
                var block = new GameObject("Block " + Word[i]).transform;
                block.SetParent(plate, false);
                block.localPosition = new Vector3(BlockX(i), trayTop + BlockSize * .5f + BlockRow(i) * BlockSize + BlockLift[i], PlateDepth * .5f);
                block.localRotation = Quaternion.Euler(0f, BlockTurn[i], 0f);
                Color wood = Woods[i];
                var body = ToyMeshes.RoundedBox(Vector3.one * BlockSize, BlockSize * BlockRound, wood, Color.Lerp(wood, SideWood, .25f),
                    Color.Lerp(wood, Color.white, .12f), wood, "SignBlock");
                Color pastel = Color.Lerp(Pastels[i], Color.white, .5f);
                var panel = ToyMeshes.RoundedSlab(BlockSize * .74f, BlockSize * .74f, BlockSize * .16f, .003f, pastel, Color.Lerp(pastel, Color.black, .08f), pastel, "SignPanel");
                owned.Add(body); owned.Add(panel);
                Part("Body", body, block);
                Part("Panel", panel, block).localPosition = new Vector3(0f, 0f, -BlockSize * .5f - .0012f);
                var go = new GameObject("Letter", typeof(RectTransform));
                go.transform.SetParent(block, false);
                go.transform.localPosition = new Vector3(0f, -BlockSize * .03f, -BlockSize * .5f - .0017f);
                var letter = go.AddComponent<TextMeshPro>();
                if (font != null) letter.font = font;
                letter.text = Word[i].ToString();
                letter.color = LetterInk;
                letter.fontStyle = FontStyles.Bold;
                letter.alignment = TextAlignmentOptions.Center;
                letter.rectTransform.sizeDelta = new Vector2(BlockSize * .66f, BlockSize * .66f); // inside the panel
                letter.enableAutoSizing = true;
                letter.fontSizeMin = .05f;
                letter.fontSizeMax = 4f;
                letter.textWrappingMode = TextWrappingModes.NoWrap;
                if (i == 0) word = letter;
                letter.ForceMeshUpdate(); // its auto-size layout now, not on the frame it first shows
                block.localScale = Vector3.zero; // each pops up out of the tray (LateUpdate)
                blocks.Add(block);
                blockRest.Add(block.localPosition);
                yield return null; BuildFrames++;
            }
            // The wind-up key on the tray's right end.
            key = new GameObject("Wind-up Key").transform;
            key.SetParent(plate, false);
            // Stem out of the side, two round bows facing the person; it turns about the stem.
            key.localPosition = new Vector3(PlateWidth * .5f, trayTop - TrayThick * .5f, PlateDepth * .5f);
            key.localScale = Vector3.one * KeySize; // 2026-11-15: a little bigger, so its winding is seen
            var stem = ToyMeshes.RoundedSlab(.03f, .008f, .004f, .008f, Gold, new Color(.8f, .58f, .18f), Gold, "KeyStem");
            var bow = ToyMeshes.Disc(.016f, .007f, Gold, new Color(.8f, .58f, .18f), "KeyBow");
            var hole = ToyMeshes.Disc(.006f, .008f, new Color(.98f, .9f, .62f), new Color(.8f, .58f, .18f), "KeyHole");
            owned.Add(stem); owned.Add(bow); owned.Add(hole);
            Part("Stem", stem, key).localPosition = new Vector3(.015f, 0f, 0f);
            for (int side = -1; side <= 1; side += 2)
            {
                var lobe = Part("Bow", bow, key);
                lobe.localPosition = new Vector3(.036f, side * .015f, 0f);
                Part("Hole", hole, lobe).localPosition = new Vector3(0f, 0f, -.001f);
            }
            // Maker's thought: a small pill with three dots, shown above its head later.
            bubble = new GameObject("Thought").transform;
            bubble.SetParent(Root, false);
            var pill = ToyMeshes.RoundedSlab(.05f, .026f, .013f, .005f, Paper, Ink, Paper, "ThoughtPill");
            var dot = ToyMeshes.Disc(.0035f, .002f, Ink, Ink, "ThoughtDot");
            var small = ToyMeshes.Disc(.004f, .003f, Paper, Ink, "ThoughtTail");
            owned.Add(pill); owned.Add(dot); owned.Add(small);
            Part("Pill", pill, bubble);
            for (int i = 0; i < 3; i++) Part("Dot", dot, bubble).localPosition = new Vector3((i - 1) * .012f, 0f, -.004f);
            Part("Tail", small, bubble).localPosition = new Vector3(-.026f, -.02f, 0f);
            bubble.gameObject.SetActive(false);
            // What the waiting crew looks up at: the middle of the plate's top.
            stageLook = new GameObject("Stage Look").transform;
            stageLook.SetParent(Root, false);
            stageLook.localPosition = new Vector3(0f, PlateHeight * .5f, PlateDepth * .5f);
            // Drawn for two frames a millimetre small (blocks and letters too): the GPU sets up the props material's
            // and the letters' first draws here instead of on the pop.
            plate.localScale = Vector3.one * WarmScale;
            foreach (var b in blocks) b.localScale = Vector3.one;
            for (int f = 0; f < 2; f++) { yield return null; BuildFrames++; }
            foreach (var b in blocks) b.localScale = Vector3.zero;
            plate.localScale = Vector3.zero;
            building = false;
        }
        private bool building;

        // The crew's shader for every prop the title builds (2026-11-13): Resources/ToyCharacterProps
        // (AgentScripts/ConfigureToyProps.cs), or the same made here if that asset is missing; the speech card's
        // Toy Surface only if the shader is not in the build.
        public const string PropsMaterialName = "ToyCharacterProps";
        private Material PropsMaterial()
        {
            var shared = Resources.Load<Material>(PropsMaterialName);
            if (shared != null) return shared;
            var shader = Shader.Find("Windups/Toy Character");
            if (shader == null) return arrival.guide.authoredBubble.GetComponent<MeshRenderer>().sharedMaterial;
            var made = new Material(shader) { name = PropsMaterialName + " (made)" };
            made.SetFloat("_VertexColor", 1f);
            made.SetFloat("_TwoSided", 1f);
            made.SetFloat("_Cull", 0f);
            made.SetFloat("_GradientAmount", .05f);
            made.SetFloat("_GradientHeight", .1f);
            owned.Add(made);
            return made;
        }
        public Material PropMaterial => material; // (checks)

        private Transform Part(string name, Mesh mesh, Transform parent)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go.transform;
        }

        // ------------------------------------------------------------ the stage (F35)

        // Where the i-th one stands on the sign (left to right: Spark on the o, Maker between s and m, Fixer on the h).
        // F67: Spark on the first s of "smiths", Maker on top of "Room" (between its two o's), Fixer on the last s.
        private Vector3 StagePoint(int index) => Root.TransformPoint(new Vector3(StageX(index), StageY(index), PlateDepth * .5f));
        private static int BlockRow(int i) => i < WordBreak ? 1 : 0; // 1: "Room" on top, 0: "smiths" on the tray
        private static float BlockX(int i) => i < WordBreak ? (i - (WordBreak - 1) * .5f) * Pitch : (i - WordBreak - (Letters - WordBreak - 1) * .5f) * Pitch;
        private static float StageX(int index) => index == 0 ? BlockX(WordBreak) : index == 1 ? 0f : index == 2 ? BlockX(Letters - 1) : (index - 1) * StageSpacing;
        private static float StageY(int index) => index == 1 ? PlateHeight * .5f : PlateHeight * .5f - BlockSize;

        // Facing the person, from a point near the plate.
        private Quaternion FacingFrom(Vector3 point)
        {
            Vector3 to = Vector3.ProjectOnPlane(arrival.handoff.meeting.Head.position - point, Vector3.up);
            return Quaternion.LookRotation(to.sqrMagnitude > 1e-6f ? to.normalized : -Root.forward, Vector3.up);
        }

        // Each one waits on the real surface straight below its place on the stage, StageBehind behind the
        // plate's face (so the hop up clears the plate's back), if that surface is a hop away; else null:
        // it will hop up from just behind the plate.
        private List<Vector3?> WaitingSpots(int count)
        {
            var room = arrival.handoff.meeting.environment;
            var list = new List<Vector3?>();
            for (int i = 0; i < count; i++)
            {
                Vector3 stage = StagePoint(i);
                Vector3 above = Root.TransformPoint(new Vector3(StageX(i), -PlateHeight * .5f, StageBehind));
                Vector3? spot = null;
                if (Physics.Raycast(above, Vector3.down, out var hit, StageRiseMax + .1f, room.GeometryMask, QueryTriggerInteraction.Ignore) &&
                    hit.normal.y > .9f && stage.y - hit.point.y <= StageRiseMax && list.TrueForAll(q => !q.HasValue || Vector3.Distance(q.Value, hit.point) > .09f))
                    spot = room.TrySameLevelNavPoint(hit.point, out var nav) && Mathf.Abs(nav.y - hit.point.y) < .03f && Vector3.Distance(nav, hit.point) < .05f ? nav : hit.point;
                list.Add(spot);
            }
            return list;
        }

        // Up onto the plate in one hop: a little stretch at take-off, an arc that passes behind the plate's
        // back, a soft squash on landing; then it stands there (re-asserted every frame: the plate is
        // world-locked and nothing else may move it) until the jump down.
        private IEnumerator HopUp(WindupCharacter character, int index, Vector3? wait)
        {
            var pet = character.movement;
            var body = WindupBody.Of(character);
            Vector3 stand = StagePoint(index);
            Vector3 from = wait ?? Root.TransformPoint(new Vector3(StageX(index), -PlateHeight * .5f + .02f, StageBehind));
            // Anticipate only while supported (F52: just a quick dip, so it reads as jumping at once). No-surface
            // fallback stays hidden until airborne.
            if (wait.HasValue) yield return Prepare(character, .07f);
            else { float until = Time.time + .07f; while (Time.time < until && !Skipping) yield return null; } // the same beat: takeoffs stay evenly spaced
            if (pet.IsHeld) { body?.EndIntro(); yield break; }
            Airborne.Add(character);
            TakeoffTimes.Add(Time.time);
            pet.Present(from, FacingFrom(from));
            ToySounds.Play(ToySounds.Cue.Pop, from + Vector3.up * .05f, .3f, 1.5f + index * .12f);
            float rise = Mathf.Max(0, stand.y - from.y);
            float seconds = Mathf.Clamp(.46f + .32f * Mathf.Sqrt(rise), .46f, .76f);
            float apex = Mathf.Max(stand.y, from.y) + .035f;
            body?.Emote(WindupBody.Excited, seconds + .2f); // F68: not a blank face in the air
            for (float t = 0; t < 1 && !Skipping; t += Time.deltaTime / seconds)
            {
                if (pet.IsHeld) { Airborne.Remove(character); body?.EndIntro(); yield break; }
                Vector3 p = Arc(from, stand, apex, t);
                pet.Present(p, FacingFrom(p));
                body?.IntroPose(Mathf.Sin(t * Mathf.PI * 2) * 3f);
                yield return null;
            }
            Airborne.Remove(character);
            if (pet.IsHeld) { body?.EndIntro(); yield break; }
            pet.Present(stand, FacingFrom(stand));
            OnStage++;
            body?.Emote(WindupBody.Happy, 1.4f); // F68: landed on the sign
            ToySounds.Play(ToySounds.Cue.Press, stand, .35f, 1.6f + index * .1f);
            ToyFx.Puff(stand + Vector3.up * .004f, .006f, 2);
            yield return Settle(character);
            while (pet.Presented && Stage != "jump" && !pet.IsHeld)
            {
                pet.Present(stand, FacingFrom(stand));
                yield return null;
            }
        }

        private IEnumerator Prepare(WindupCharacter character, float seconds)
        {
            var body = WindupBody.Of(character);
            for (float t = 0; t < 1 && !Skipping; t += Time.deltaTime / seconds)
            {
                if (character.movement.IsHeld) { body?.EndIntro(); yield break; }
                body?.IntroPose(4f * Mathf.Sin(Mathf.PI * t));
                yield return null;
            }
            body?.IntroPose();
        }

        private IEnumerator Settle(WindupCharacter character)
        {
            var body = WindupBody.Of(character);
            for (float t = 0; t < 1 && !Skipping; t += Time.deltaTime / .26f)
            {
                if (character.movement.IsHeld) { body?.EndIntro(); yield break; }
                body?.IntroPose(5f * Mathf.Sin(Mathf.PI * t) * (1f-t));
                yield return null;
            }
            body?.IntroPose();
        }

        // A continuous ballistic arc with an actual apex, then eased clock for quiet endpoints.
        // The old formula overshot its declared apex for unequal endpoint heights.
        private static Vector3 Arc(Vector3 a, Vector3 b, float apex, float t)
        {
            t = Mathf.SmoothStep(0, 1, Mathf.Clamp01(t));
            float up = Mathf.Sqrt(Mathf.Max(.0001f, apex-a.y));
            float down = Mathf.Sqrt(Mathf.Max(.0001f, apex-b.y));
            float peak = up / (up+down);
            Vector3 p = Vector3.Lerp(a,b,t);
            float u = t < peak ? (peak-t)/peak : (t-peak)/(1-peak);
            p.y = apex - (t < peak ? apex-a.y : apex-b.y) * u*u;
            return p;
        }

        private static float Overshoot(float t)
        {
            t = Mathf.Clamp01(t);
            const float s = 1.9f;
            t -= 1f;
            return t * t * ((s + 1f) * t + s) + 1f;
        }

        // Where each one lands: the arrival seats the room offers near the person (the desk first, as
        // the start without the intro used), else the surface below the plate, spread side by side.
        private List<Vector3> Landings(int count, Transform head)
        {
            var list = new List<Vector3>();
            var room = arrival.handoff.meeting.environment;
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            forward = forward.sqrMagnitude > .001f ? forward.normalized : Vector3.forward;
            var seats = new ArrivalSeats(room, head.position, forward);
            for (int i = 0; i < count; i++)
            {
                float angle = count > 1 ? Mathf.Lerp(-40f, 40f, i / (count - 1f)) : 0f;
                var ranked = seats.Ranked(angle, room);
                if (ranked.Count == 0) ranked = seats.Ranked(angle);
                foreach (var p in ranked)
                {
                    bool clear = list.TrueForAll(q => Vector3.Distance(p, q) > .2f);
                    bool reachable = Vector3.ProjectOnPlane(p - Root.position, Vector3.up).magnitude < 1.4f;
                    if (clear && reachable) { list.Add(p); break; }
                }
                if (list.Count <= i) break;
            }
            if (list.Count == count) return list;
            list.Clear();
            Vector3 right = Root.right, toUser = Vector3.ProjectOnPlane(head.position - Root.position, Vector3.up).normalized;
            Vector3 below = Root.position + toUser * .12f;
            float baseY = Root.position.y - 1.2f;
            if (Physics.Raycast(below + Vector3.up * .05f, Vector3.down, out var hit, 3f, room.GeometryMask, QueryTriggerInteraction.Ignore)) baseY = hit.point.y;
            for (int i = 0; i < count; i++)
            {
                Vector3 want = new Vector3(below.x, baseY, below.z) + right * ((i - (count - 1) * .5f) * .2f);
                list.Add(room.TrySameLevelNavPoint(want, out var p) && Mathf.Abs(p.y - baseY) < .15f ? p : want);
            }
            return list;
        }

        // Down from the plate onto its seat in one hop (F35: driven, not thrown, so it lands exactly on the seat; a
        // thrown hop could hit something standing on the desk and fall to the floor). The arc goes higher if
        // something is in the way; it lands softly and walks on from there.
        public int HoppedDown { get; private set; }
        private IEnumerator HopDown(WindupCharacter character, Vector3 target)
        {
            var pet = character.movement;
            var body = WindupBody.Of(character);
            var room = arrival.handoff.meeting.environment;
            Vector3 from = pet.transform.position;
            float apex = Mathf.Max(from.y, target.y) + .07f;
            foreach (float extra in new[] { 0f, .1f, .22f, .36f })
            {
                apex = Mathf.Max(from.y, target.y) + .07f + extra;
                var arc = new Vector3[12];
                for (int k = 0; k < arc.Length; k++) arc[k] = Arc(from, target, apex, (k + 1f) / arc.Length);
                if (room.PathIsClear(arc)) break;
            }
            float span = Vector3.ProjectOnPlane(target - from, Vector3.up).magnitude, drop = Mathf.Abs(from.y - target.y);
            float seconds = Mathf.Clamp(.45f + .3f * Mathf.Sqrt(Mathf.Max(drop, span * .6f)), .45f, .9f);
            // F68 (2026-11-14, the user: "they jump down off the Roomsmiths sign with blank faces, it looks odd"):
            // excited as they take off, happy through the landing.
            body?.Emote(WindupBody.Excited, .12f + seconds * .5f);
            yield return Prepare(character, .12f);
            if (pet.IsHeld) { body?.EndIntro(); HoppedDown++; yield break; }
            Airborne.Add(character);
            ToySounds.Play(ToySounds.Cue.Whoosh, from, .35f, 1.5f);
            for (float t = 0f; t < 1f; t += Time.deltaTime / seconds)
            {
                if (pet.IsHeld) { Airborne.Remove(character); body?.EndIntro(); HoppedDown++; yield break; }
                Vector3 p = Arc(from, target, apex, t);
                pet.Present(p, FacingFrom(p));
                body?.IntroPose(Mathf.Sin(t * Mathf.PI * 2) * 3f);
                if (t > .5f && body != null && body.Face != WindupBody.Happy && body.Face != WindupBody.Blink) body.Emote(WindupBody.Happy, seconds * .5f + 1.6f);
                yield return null;
            }
            Airborne.Remove(character);
            if (pet.IsHeld) { body?.EndIntro(); HoppedDown++; yield break; }
            pet.Present(target, FacingFrom(target));
            if (pet.SettleAt(target))
            {
                yield return Settle(character);
                ToySounds.Play(ToySounds.Cue.Press, target, .45f, 1.4f);
                ToyFx.Puff(target + Vector3.up * .004f, .006f, 2);
            }
            body?.EndIntro();
            HoppedDown++;
        }

        private void ShowCaption(string text)
        {
            var guide = arrival.guide.authoredBubble;
            // F52: the words first, then the card (shown first, it popped up with its old scene text for a moment).
            guide.SetText(arrival.guide.characterName, text, "");
            guide.title.text = text;
            guide.gameObject.SetActive(true);
            WindupBody.Express(arrival.guide, text, 2f);
        }

        private IEnumerator Turn(Transform t, float degrees, float seconds)
        {
            var start = t.localRotation;
            for (float k = 0f; k < 1f; k += Time.deltaTime / Mathf.Max(.01f, seconds))
            {
                if (Skipping) break;
                t.localRotation = start * Quaternion.Euler(Mathf.SmoothStep(0f, degrees, k), 0f, 0f);
                yield return null;
            }
            t.localRotation = start * Quaternion.Euler(degrees, 0f, 0f);
        }

        // 2026-11-15: the key winds before the blocks come: a click, a quarter turn, a beat - WindClicks times (the
        // short version a little quicker). Each click knocks the tray (windKickAt, LateUpdate).
        public const int WindClicks = 3;
        public const float WindTurn = .11f, WindRest = .07f, WindDegrees = 90f, KeySize = 1.35f;
        public int KeyClicks { get; private set; }
        public float WoundAt { get; private set; } = -1f;
        private float blocksFrom = -1f, windKickAt = -1f;

        private IEnumerator WindKey()
        {
            for (int i = 0; i < WindClicks && !Skipping && key != null; i++)
            {
                KeyClicks++;
                windKickAt = Time.time;
                ToySounds.Play(ToySounds.Cue.Ratchet, key.position, .8f, 1f + i * .07f);
                yield return Turn(key, WindDegrees, WindTurn * scale);
                yield return Wait(WindRest);
            }
            WoundAt = Time.time;
        }

        private IEnumerator WindAndLetGo()
        {
            yield return WindKey();
            yield return UnwindKey();
        }

        // Let go: it spins back the way it was wound, fast and then slower, whirring.
        private IEnumerator UnwindKey()
        {
            if (key == null) yield break;
            if (!Skipping) ToySounds.Play(ToySounds.Cue.Whirr, key.position, .6f, 1f);
            var start = key.localRotation;
            float total = -WindDegrees * WindClicks - 360f, seconds = .8f * scale;
            for (float k = 0f; k < 1f && key != null && !Skipping; k += Time.deltaTime / seconds)
            {
                float e = 1f - (1f - k) * (1f - k); // fast first
                key.localRotation = start * Quaternion.Euler(total * e, 0f, 0f);
                yield return null;
            }
            if (key != null) key.localRotation = start * Quaternion.Euler(total, 0f, 0f);
        }

        private IEnumerator LetterSound(int i)
        {
            yield return new WaitForSeconds((BlockLead + i * BlockStep + BlockPop * .5f) * scale);
            if (!Skipping && Root != null) ToySounds.Play(ToySounds.Cue.Pop, Root.position, .18f, 1.6f + i * .05f);
        }

        // Waits that a skip cuts short.
        private IEnumerator Wait(float seconds)
        {
            float until = Time.time + seconds * scale;
            while (Time.time < until && !Skipping) yield return null;
        }

        private void Update()
        {
            if (PlateMode && Done) { KeepCrewInAir(); return; }
            if (Root == null || Done) return;
            // Any pinch skips ahead.
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !hand.IsConnected) continue;
                bool pinching = hand.GetIndexFingerIsPinching();
                wasPinching.TryGetValue(hand, out bool before);
                wasPinching[hand] = pinching;
                if (pinching && !before && Stage != "jump" && Stage != "done") Skip();
            }
#if UNITY_EDITOR
            if (UnityEngine.InputSystem.Keyboard.current?.spaceKey.wasPressedThisFrame == true) Skip();
#endif
        }

        // F78: where each lands in the air (the tray's top, a little wider apart than on the sign).
        public Vector3 AirSeat(int index) => Root.TransformPoint(new Vector3(StageX(index) * AirSpread, -PlateHeight * .5f + TrayThick, PlateDepth * .5f));

        // Down into the air: the same arc and faces as the hop up, then it simply stays there (kinematic, presented).
        private IEnumerator HopToAir(WindupCharacter character, Vector3 to)
        {
            var pet = character.movement;
            var body = WindupBody.Of(character);
            if (pet.IsHeld) { body?.EndIntro(); HoppedDown++; yield break; }
            Vector3 from = pet.transform.position;
            Airborne.Add(character);
            float seconds = .42f, apex = Mathf.Max(from.y, to.y) + .04f;
            body?.Emote(WindupBody.Excited, seconds + .2f);
            ToySounds.Play(ToySounds.Cue.Pop, from + Vector3.up * .05f, .3f, 1.5f);
            for (float t = 0f; t < 1f && !Skipping; t += Time.deltaTime / seconds)
            {
                if (pet.IsHeld) { Airborne.Remove(character); body?.EndIntro(); HoppedDown++; yield break; }
                Vector3 p = Arc(from, to, apex, t);
                pet.Present(p, FacingFrom(p));
                body?.IntroPose(Mathf.Sin(t * Mathf.PI * 2) * 3f);
                yield return null;
            }
            Airborne.Remove(character);
            if (pet.IsHeld) { body?.EndIntro(); HoppedDown++; yield break; }
            pet.Present(to, FacingFrom(to));
            body?.Emote(WindupBody.Happy, 1.4f);
            ToySounds.Play(ToySounds.Cue.Press, to, .35f, 1.6f);
            ToyFx.Puff(to + Vector3.up * .004f, .006f, 2);
            yield return Settle(character);
            body?.EndIntro();
            HoppedDown++;
        }

        // F78: with no room nothing holds a teammate up but this: one let go (or found falling) stays where it is.
        private readonly List<WindupCharacter> stayCrew = new List<WindupCharacter>();
        private float airCheckAt;
        private void KeepCrewInAir()
        {
            if (Time.time < airCheckAt) return;
            airCheckAt = Time.time + .1f;
            foreach (var c in stayCrew)
            {
                if (c == null || c.movement.IsHeld || Airborne.Contains(c)) continue;
                var pet = c.movement;
                if (pet.State != RoomCapsule.CapsulePetController.MotionState.Falling && pet.Presented) continue;
                if (pet.State == RoomCapsule.CapsulePetController.MotionState.Falling || !pet.Presented)
                {
                    pet.Present(pet.transform.position, FacingFrom(pet.transform.position));
                    HeldInAir++;
                }
            }
        }

        public Vector3 StandPoint(int index) => StagePoint(index);

        private void LateUpdate()
        {
            if (Root == null || !Root.gameObject.activeSelf || building || plate == null || bubble == null) return;
            float now = Time.time;
            // Plate (F52): springs up out of the surface - rising from PopRise below, stretched tall on the way
            // up, then a squash and a little wobble as it settles - and folds away at the end.
            float k = plateShownAt < 0f ? 0f : (now - plateShownAt) / (PopSeconds * scale);
            float pop = plateShownAt < 0f ? 0f : Overshoot(k);
            float rise = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(k * 1.3f));
            float settle = Mathf.Clamp01(k / 1.7f);
            float wobble = plateShownAt < 0f || settle >= 1f ? 0f : Mathf.Sin(settle * Mathf.PI * 3f) * (1f - settle);
            if (plateGoneAt > 0f) pop *= 1f - Mathf.SmoothStep(0f, 1f, (now - plateGoneAt) / .3f);
            pop = Mathf.Max(0f, pop);
            plate.localPosition = Vector3.down * PopRise * rise;
            float kick = windKickAt < 0f ? 0f : Mathf.Exp(-(now - windKickAt) / .06f) * .03f; // 2026-11-15: a key click knocks the tray
            plate.localScale = new Vector3(pop * (1f - .12f * wobble + kick * .5f), pop * (1f + .24f * wobble - kick), pop);
            // Thought bubble above Maker's head.
            if (bubble.gameObject.activeSelf)
            {
                var maker = WindupBody.Of(arrival.guide);
                Vector3 at = maker != null ? maker.HeadTop + Vector3.up * .035f + Root.right * .035f : Root.position + Vector3.up * .15f;
                bubble.SetPositionAndRotation(at, Root.rotation);
                bubble.localScale = Vector3.one * Overshoot((now - bubbleAt) / .25f);
            }
            // Blocks: each pops up out of the tray with a little overshoot, 55 ms apart.
            if (lettersSettled || blocks.Count == 0 || lettersFrom < 0f) return;
            bool moving = false;
            for (int i = 0; i < blocks.Count; i++)
            {
                // quick (all up in about half a second) so every block stands before the crew lands on it
                // "smiths" (on the tray) first, then "Room" onto it.
                int order = i >= WordBreak ? i - WordBreak : (Letters - WordBreak) + i;
                // (from blocksFrom: with the plate's pop)
                float kk = Skipping ? 1f : blocksFrom < 0f ? 0f : (now - blocksFrom - (BlockLead + order * BlockStep + (i < WordBreak ? WordPause : 0f)) * scale) / (BlockPop * scale);
                if (kk < 1f) moving = true;
                blocks[i].localScale = Vector3.one * Mathf.Max(0f, Overshoot(kk));
                blocks[i].localPosition = blockRest[i] - Vector3.up * (1f - Mathf.Clamp01(kk)) * BlockSize * .5f;
            }
            if (!moving) lettersSettled = true; // settled: stop touching them
        }

        private void OnDestroy()
        {
            foreach (var pair in lookBefore) if (pair.Key != null) { WindupBody.Of(pair.Key)?.EndIntro(); pair.Key.lookTarget = pair.Value; }
            ClearTools();
            foreach (var o in owned) if (o != null) Destroy(o);
            if (Root != null) Destroy(Root.gameObject);
        }
    }
}
