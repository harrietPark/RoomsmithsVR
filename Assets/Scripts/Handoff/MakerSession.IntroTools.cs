using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    // 2026-11-13 (the user, after the Quest try: "introduce the tools we need too - Fixer has the tape AND the gear, Maker
    // the sketchbook AND the notebook, Spark can take textures too; say what each tool is FOR, not how to touch it"):
    // the team introduction (WindupsArrival.IntroduceTeam) walks through each teammate in a few short lines, and while
    // a line is said that teammate holds up the tool it talks about:
    //  - Maker: its sketchbook (where shapes are sketched together), its notebook (every idea, size and choice kept);
    //  - Fixer: its tape measure (pulled out and snapped back), its settings gear;
    //  - Spark: its magnifier (ideas in the room), a little palette that catches a colour and a texture, its frame.
    // How to touch each one is taught where it is first needed (the F49 lessons, the F55 tool hints), not here.
    // Props are small Toy meshes held with WindupBody.Hold within HandReach of the body; nothing runs per frame once
    // the introduction is over.
    public sealed partial class MakerSession
    {
        public struct IntroStep
        {
            public string line, tool;
            public float seconds;
            public IntroStep(string line, string tool, float seconds) { this.line = line; this.tool = tool; this.seconds = seconds; }
        }

        public static string IntroOpening => WindupsNetwork.Offline ? OfflineIntroOpening : "Hi! We make things with you.\nWe each have tools to help.";
        // (Quest 2026-11-13: "the mic is not there yet during the introduction": the closing line no longer shows it.)
        public static string IntroClosing => WindupsNetwork.Offline ? OfflineIntroClosing : "That's our team!\nLet's make something together.";
        // F78 airplane mode: the same three, each with the thing it collects.
        public const string OfflineIntroOpening = "Hi! No internet today,\nso let's collect ideas.";
        public const string OfflineIntroClosing = "We'll make them real\nwhen we're back online.";

        public static IntroStep[] IntroSteps(Crew crew)
        {
            if (WindupsNetwork.Offline)
                switch (crew)
                {
                    case Crew.Maker: return new[] { new IntroStep("I'm Maker.\nDraw ideas in my sketchbook.", "sketchbook", 3.2f) };
                    case Crew.Fixer: return new[] { new IntroStep("This is Fixer.\nIt hands you post-its to type on.", null, 3f) };
                    default: return new[] { new IntroStep("This is Spark.\nIts frame takes pictures\nof things around you.", "frame", 3.4f) };
                }
            switch (crew)
            {
                case Crew.Maker:
                    return new[]
                    {
                        new IntroStep("I'm Maker.\nI turn your idea into a real thing.", null, 3f),
                        new IntroStep("My sketchbook is where\nwe sketch shapes together.", "sketchbook", 3.2f),
                        new IntroStep("My notebook keeps every idea,\nsize and choice we make,", "notebook", 3.2f),
                        new IntroStep("so you can look back\nor make it again.", "notebook", 2.8f),
                    };
                case Crew.Fixer:
                    return new[]
                    {
                        new IntroStep("This is Fixer.\nIt makes sure your thing works.", null, 3f),
                        new IntroStep("Its tape measures your real stuff,\nso what we make fits.", "tape", 3.4f),
                        new IntroStep("Its gear holds your settings:\nvoice, sounds, tips, start over.", "gear", 3.4f),
                    };
                default:
                    return new[]
                    {
                        new IntroStep("This is Spark.\nIt finds ideas around you.", null, 2.8f),
                        new IntroStep("Its magnifier looks for ideas\nin the things in your room.", "magnifier", 3.4f),
                        new IntroStep("It catches colours and textures\nfrom real things for your palette.", "palette", 3.6f),
                        new IntroStep("Its frame shows us\nthe shape of a real thing.", "frame", 3f),
                    };
            }
        }

        public string IntroToolShown => introTool;
        public readonly List<string> IntroToolLog = new List<string>(); // "Crew:tool" in the order shown (checks)
        private string introTool;
        private Crew introToolCrew;
        private Coroutine introProp;
        private readonly Dictionary<string, Transform> introProps = new Dictionary<string, Transform>();

        // WindupsArrival.IntroduceTeam: `characterName` holds up `tool` for `seconds` (null puts any tool away).
        // `untilNext` (the introduction waits for the person's >): held until the next call, the moves repeating every
        // `seconds`; the same tool asked again (< back onto a line about it) just stays up.
        private bool introUntilNext;
        public void ShowIntroTool(string characterName, string tool, float seconds, bool untilNext = false)
        {
            EnsureToolProps();
            if (!MakerRoles.TryForCharacter(characterName, out var crew)) return;
            if (untilNext && !string.IsNullOrEmpty(tool) && tool == introTool && crew == introToolCrew &&
                (introProp != null || tool == "sketchbook" || tool == "gear" || tool == "magnifier")) return;
            introUntilNext = untilNext;
            if (introProp != null) { StopCoroutine(introProp); introProp = null; EndIntroProp(introToolCrew); }
            if (tool != "sketchbook" && toolBook) toolUntil[Crew.Maker] = 0f;
            if (tool != "gear") toolUntil[Crew.Fixer] = 0f;
            if (tool != "magnifier") toolUntil[Crew.Spark] = 0f;
            introTool = tool;
            introToolCrew = crew;
            if (string.IsNullOrEmpty(tool)) return;
            IntroToolLog.Add(crew + ":" + tool);
            float shown = untilNext ? 3600f : seconds;
            switch (tool)
            {
                case "sketchbook": ShowToolFor(Crew.Maker, shown); break;
                case "gear": ShowToolFor(Crew.Fixer, shown); break;
                case "magnifier": ShowToolFor(Crew.Spark, shown); break;
                default: introProp = StartCoroutine(HoldIntroProp(crew, tool, seconds)); break;
            }
        }

        // The introduction is over: every tool goes back.
        public void EndIntroTools()
        {
            if (introProp != null) { StopCoroutine(introProp); introProp = null; EndIntroProp(introToolCrew); }
            introTool = null;
            foreach (var crew in ToolOrder) toolUntil[crew] = 0f;
            if (notebookClosed != null && !Begun) notebookClosed.gameObject.SetActive(false);
        }

        private void EndIntroProp(Crew crew)
        {
            var body = Body(crew);
            body?.Hold(true, null);
            body?.Hold(false, null);
            foreach (var pair in introProps)
                if (pair.Value != null && pair.Value.gameObject.activeSelf) { ToyFx.Puff(pair.Value.position, .006f, 2); pair.Value.gameObject.SetActive(false); }
            if (notebookClosed != null && !Begun && crew == Crew.Maker) notebookClosed.gameObject.SetActive(false);
        }

        private IEnumerator HoldIntroProp(Crew crew, string tool, float seconds)
        {
            if (!cast.TryGetValue(crew, out var character) || character == null) yield break;
            var body = Body(crew);
            if (body == null) yield break;
            Transform prop = tool == "notebook" ? NotebookProp() : IntroProp(tool);
            if (prop == null) yield break;
            prop.gameObject.SetActive(true);
            prop.localScale = Vector3.one * .05f;
            StartCoroutine(Grow(prop, 1f, .22f));
            ToySounds.Play(tool == "notebook" ? ToySounds.Cue.Paper : ToySounds.Cue.Pop, character.transform.position + Vector3.up * .06f, .3f, 1.4f);
            bool snapped = false, dropped = false, forever = introUntilNext;
            for (float start = Time.time; (forever || Time.time - start < seconds) && introTool == tool; )
            {
                if (character.movement != null && character.movement.IsHeld) break;
                // Waiting for the person's >: the tape and the frame's scan repeat, the palette keeps its colour.
                float elapsed = (Time.time - start) / Mathf.Max(.1f, seconds);
                float t = !forever ? elapsed : tool == "palette" || tool == "notebook" ? Mathf.Min(elapsed, .99f) : Mathf.Repeat(elapsed, 1f);
                if (forever && t < .05f) snapped = false;
                Vector3 at = character.transform.position;
                Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - at, Vector3.up) : Vector3.back;
                toUser = toUser.sqrMagnitude > 1e-6f ? toUser.normalized : Vector3.back;
                Vector3 right = Vector3.Cross(Vector3.up, toUser); // the person's left as they face it
                // The cover or face of the prop toward the person, tipped back a little (as Maker offers its sketchbook).
                Quaternion facing = Quaternion.AngleAxis(-15f, right) * Quaternion.LookRotation(-toUser, Vector3.up);
                switch (tool)
                {
                    case "notebook":
                    {
                        Vector3 centre = at + toUser * IntroPropOut + Vector3.up * .05f; // (lower: out in front its top hid the chin)
                        prop.SetPositionAndRotation(centre, facing);
                        Grip(body, centre, facing, .025f + .0065f);
                        break;
                    }
                    case "tape":
                    {
                        // The case in one mitten; the other pulls the blade out sideways and lets it snap back.
                        float pull = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.08f, .38f, t)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.72f, .8f, t)));
                        if (!snapped && t > .72f) { snapped = true; ToySounds.Play(ToySounds.Cue.Whoosh, at + Vector3.up * .06f, .3f, 2f); }
                        // the case in the left mitten (its own left), the blade pulled across the front toward its right
                        Vector3 caseAt = at + toUser * .034f - right * .034f + Vector3.up * .058f;
                        prop.SetPositionAndRotation(caseAt, Quaternion.LookRotation(-toUser, Vector3.up));
                        var blade = prop.Find("Blade");
                        var hook = prop.Find("Hook");
                        float length = .012f + pull * .062f;
                        if (blade != null) { blade.localScale = new Vector3(length / .01f, 1f, 1f); blade.localPosition = new Vector3(-length * .5f - .012f, -.004f, 0f); }
                        if (hook != null) hook.localPosition = new Vector3(-length - .012f, -.004f, 0f);
                        body.Hold(false, new Pose(Clamp(at, caseAt - right * .012f), body.NaturalGrip(false, caseAt, 45f)));
                        Vector3 end = hook != null ? hook.position : caseAt + right * length;
                        body.Hold(true, new Pose(Clamp(at, end + right * .006f), body.NaturalGrip(true, end, 45f)));
                        break;
                    }
                    case "palette":
                    {
                        // A little palette catches a colour, then a texture ball rolls on beside it.
                        Vector3 centre = at + toUser * IntroPropOut + Vector3.up * .05f;
                        prop.SetPositionAndRotation(centre, Quaternion.AngleAxis(-35f, right) * Quaternion.LookRotation(-toUser, Vector3.up));
                        var drop = prop.Find("New Colour");
                        var ball = prop.Find("Texture Ball");
                        if (drop != null) drop.localScale = Vector3.one * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.25f, .4f, t));
                        // (a primitive sphere is 1 m across: scaled to 1 it was a metre-wide ball on the Quest)
                        if (ball != null) ball.localScale = Vector3.one * IntroBallSize * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.55f, .7f, t));
                        if (!dropped && t > .25f) { dropped = true; ToySounds.Play(ToySounds.Cue.Drip, centre, .3f, 1.3f); }
                        body.Hold(true, new Pose(Clamp(at, centre + right * .03f - Vector3.up * .004f), body.NaturalGrip(true, centre, 45f)));
                        body.Hold(false, null);
                        break;
                    }
                    case "frame":
                    {
                        // The frame held up in both mittens below the face (its top bar must not cross the face, as the
                        // magnifier once did); a scan line runs down it once (it takes a shape).
                        float eyeHeight = Mathf.Max(.07f, body.HeadTop.y - at.y - .058f);
                        Vector3 centre = at + toUser * .04f + Vector3.up * Mathf.Max(.03f, eyeHeight - .03f - IntroFrameSize * .5f);
                        prop.SetPositionAndRotation(centre, facing);
                        var scan = prop.Find("Scan");
                        if (scan != null) { scan.gameObject.SetActive(t > .3f && t < .8f); scan.localPosition = new Vector3(0f, Mathf.Lerp(.42f, -.42f, Mathf.InverseLerp(.3f, .8f, t)) * IntroFrameSize, -.002f); }
                        Grip(body, centre, facing, IntroFrameSize * .5f + .004f);
                        break;
                    }
                }
                yield return null;
            }
            introProp = null;
            EndIntroProp(crew);
        }

        // Both mittens on the prop's side edges, with the prop's own turn (the way Maker offers its sketchbook).
        private void Grip(WindupBody body, Vector3 centre, Quaternion rot, float half)
        {
            Vector3 at = body.transform.position;
            body.Hold(true, new Pose(Clamp(at, centre + rot * new Vector3(-half, -.0115f, .011f)), rot));
            body.Hold(false, new Pose(Clamp(at, centre + rot * new Vector3(half, -.0115f, .011f)), rot));
        }

        // A hand never leaves HandReach of the body's axis (the mittens have no arms).
        private static Vector3 Clamp(Vector3 axis, Vector3 hand)
        {
            Vector3 flat = Vector3.ProjectOnPlane(hand - axis, Vector3.up);
            if (flat.magnitude <= HandReach) return hand;
            return axis + flat.normalized * HandReach + Vector3.up * Vector3.Dot(hand - axis, Vector3.up);
        }

        private Transform NotebookProp()
        {
            EnsureNotebookObjects();
            return notebookClosed;
        }

        private const float IntroFrameSize = .056f;
        private const float IntroBallSize = .012f;   // the palette's texture ball, 1.2 cm
        private const float IntroPaletteScale = .38f; // the designed palette (15 cm) at about the old mini disc's 6.5 cm
        public const float IntroPropOut = .074f;    // notebook and palette in front of the body's axis (were 3.6/4 cm: inside the body)
        private Transform IntroProp(string tool)
        {
            if (introProps.TryGetValue(tool, out var prop) && prop != null) return prop;
            prop = new GameObject("Intro " + tool).transform;
            prop.SetParent(transform, false);
            Transform Piece(string name, Mesh mesh, Vector3 local, Quaternion rot)
            {
                owned.Add(mesh);
                var t = Part(name, mesh);
                t.SetParent(prop, false);
                t.localPosition = local;
                t.localRotation = rot;
                return t;
            }
            switch (tool)
            {
                case "tape":
                {
                    Color yellow = new Color(1f, .8f, .16f), dark = new Color(.82f, .6f, .06f), steel = new Color(.85f, .86f, .9f);
                    Piece("Case", ToyMeshes.RoundedSlab(.026f, .026f, .008f, .012f, yellow, dark, yellow, "TapeCase"), Vector3.zero, Quaternion.identity);
                    Piece("Case Dot", ToyMeshes.Disc(.006f, .002f, dark, dark, "TapeDot"), new Vector3(0f, 0f, -.0065f), Quaternion.identity);
                    var blade = Piece("Blade", ToyMeshes.RoundedSlab(.01f, .005f, .001f, .0012f, yellow, dark, yellow, "TapeBlade"), new Vector3(-.017f, -.004f, 0f), Quaternion.identity);
                    blade.localScale = new Vector3(1f, 1f, 1f);
                    Piece("Hook", ToyMeshes.RoundedSlab(.003f, .008f, .001f, .003f, steel, steel, steel, "TapeHook"), new Vector3(-.024f, -.004f, 0f), Quaternion.identity);
                    break;
                }
                case "palette":
                {
                    Color wood = new Color(.86f, .68f, .45f), woodEdge = new Color(.68f, .5f, .3f);
                    // 2026-11-15: the designed palette, small (6 cm), with its own paints; the old disc without it.
                    if (PaletteModelPiece(prop, IntroPaletteScale, true) == null)
                    {
                        var board = Piece("Board", ToyMeshes.Disc(.026f, .003f, wood, woodEdge, "MiniPalette"), Vector3.zero, Quaternion.identity);
                        board.localScale = new Vector3(1.25f, 1f, 1f);
                        Piece("Blob A", ToyMeshes.Disc(.0055f, .003f, new Color(.89f, .34f, .3f), new Color(.7f, .25f, .22f), "MiniBlob"), new Vector3(-.016f, .008f, -.003f), Quaternion.identity);
                        Piece("Blob B", ToyMeshes.Disc(.0055f, .003f, new Color(.36f, .69f, .85f), new Color(.25f, .5f, .65f), "MiniBlob"), new Vector3(-.004f, .014f, -.003f), Quaternion.identity);
                    }
                    var drop = Piece("New Colour", ToyMeshes.Disc(.0062f, .0035f, new Color(.95f, .7f, .24f), new Color(.75f, .52f, .14f), "MiniBlob"), new Vector3(.01f, .01f, -.0035f), Quaternion.identity);
                    drop.localScale = Vector3.zero;
                    var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(ball.GetComponent<Collider>());
                    ball.name = "Texture Ball";
                    ball.transform.SetParent(prop, false);
                    ball.transform.localPosition = new Vector3(.016f, -.008f, -.007f);
                    ball.transform.localScale = Vector3.zero;
                    var r = ball.GetComponent<MeshRenderer>();
                    r.sharedMaterial = IntroTextureLook();
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    break;
                }
                case "frame":
                {
                    Color green = MakerRoles.Color(Crew.Spark), edge = Color.Lerp(green, Color.black, .25f);
                    const float size = IntroFrameSize, bar = .006f;
                    Piece("Top", ToyMeshes.RoundedSlab(size, bar, .002f, .004f, green, edge, green, "FrameBar"), new Vector3(0f, size * .5f, 0f), Quaternion.identity);
                    Piece("Bottom", ToyMeshes.RoundedSlab(size, bar, .002f, .004f, green, edge, green, "FrameBar"), new Vector3(0f, -size * .5f, 0f), Quaternion.identity);
                    Piece("Left", ToyMeshes.RoundedSlab(bar, size, .002f, .004f, green, edge, green, "FrameBar"), new Vector3(-size * .5f, 0f, 0f), Quaternion.identity);
                    Piece("Right", ToyMeshes.RoundedSlab(bar, size, .002f, .004f, green, edge, green, "FrameBar"), new Vector3(size * .5f, 0f, 0f), Quaternion.identity);
                    Piece("Button", ToyMeshes.Disc(.004f, .003f, new Color(.92f, .28f, .25f), new Color(.7f, .2f, .18f), "FrameButton"), new Vector3(size * .5f - .002f, size * .5f + .004f, -.002f), Quaternion.identity);
                    var scan = Piece("Scan", ToyMeshes.RoundedSlab(size - bar, .0018f, .0008f, .0008f, new Color(.6f, 1f, .75f), new Color(.6f, 1f, .75f), new Color(.6f, 1f, .75f), "FrameScan"), Vector3.zero, Quaternion.identity);
                    scan.gameObject.SetActive(false);
                    break;
                }
            }
            prop.gameObject.SetActive(false);
            introProps[tool] = prop;
            return prop;
        }

        private Material introTextureLook;
        private Material IntroTextureLook()
        {
            if (introTextureLook != null) return introTextureLook;
            // A little wood-grain ball, made once (the same look as a palette's texture swatch).
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGB24, true) { wrapMode = TextureWrapMode.Repeat, name = "Intro Grain" };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float g = Mathf.Sin((x + Mathf.Sin(y * .3f) * 3f) * .55f) * .5f + .5f;
                    px[y * n + x] = Color32.Lerp(new Color32(150, 98, 52, 255), new Color32(205, 150, 92, 255), g);
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            owned.Add(tex);
            introTextureLook = TileMaterial(tex, new Vector2(2f, 1f));
            return introTextureLook;
        }
    }
}
