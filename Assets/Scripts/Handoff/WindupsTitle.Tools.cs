using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    // F52 (2026-11-12, the user: "when the characters are introduced, show what each one is about too"): on the
    // title plate each teammate pulls out its own tool and uses it for a moment instead of a plain wave:
    //  - Spark (Creativity) lifts a magnifier up beside its face (2026-11-13: never in front of it, as the person sees
    //    it) and shows it to the person - it finds things in the room;
    //  - Maker (Decisions) paints two strokes in the air with a big brush, leaving blue dabs - it makes;
    //  - Fixer (Caution) pulls a yellow tape measure out of its case and lets it snap back, then nods - it
    //    measures and checks the fit.
    // A tool pops into the mitten with a puff and puffs away before the jump down. The mittens are posed in the
    // body's space (WindupBody.IntroHands) and kept within ToolReach of the body axis; the tools follow the
    // mittens after the hand pose (NotebookPropFollower, IntroTapeBlade).
    public sealed partial class WindupsTitle
    {
        public const float ToolStagger = .28f, ToolSeconds = 1.75f, ToolReach = .058f;
        public static readonly Color Wood = new Color(.86f, .62f, .38f), WoodDark = new Color(.62f, .41f, .24f),
            Brass = new Color(.93f, .72f, .3f), BrassDark = new Color(.7f, .5f, .18f), Glass = new Color(.8f, .94f, .99f),
            Steel = new Color(.82f, .84f, .88f), SteelDark = new Color(.55f, .57f, .62f), Bristle = new Color(.95f, .86f, .66f),
            MakerBlue = new Color(.2f, .48f, .95f), TapeYellow = new Color(1f, .8f, .16f), TapeDark = new Color(.82f, .6f, .06f);

        public readonly List<string> ToolsShown = new List<string>();
        public float ToolReachMax { get; private set; }
        private readonly List<GameObject> tools = new List<GameObject>();
        private readonly Dictionary<(WindupBody, bool), Vector3> palms = new Dictionary<(WindupBody, bool), Vector3>();

        private float ToolDuration => ToolSeconds * (ShortVersion ? .6f : 1f);

        private bool Interrupted(WindupCharacter character) => Skipping || Stage != "hello" || character == null || character.movement.IsHeld;

        // `away`: from the middle of the stage to this one (zero for the one in the middle). 2026-11-13 (the user:
        // "in the title the characters and the things overlap"): every tool used to come out on the inner side,
        // into the neighbour; now it comes out on the outer side (the middle one works in front of itself).
        private IEnumerator ShowTool(WindupCharacter character, float delay, Vector3 away, Quaternion facing)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            if (Interrupted(character)) yield break;
            var body = WindupBody.Of(character);
            if (body == null || body.RigHand(true) == null || body.RigHand(false) == null) yield break;
            string role = character.characterName;
            string what = role == "Creativity" ? "magnifier" : role == "Decisions" ? "brush" : "tape measure";
            GreetingLog.Add(role + ":" + what);
            ToolsShown.Add(what);
            // The outer side in the body's space: +1 = its own right (+x), -1 = its left, 0 = in the middle (judged
            // with the way it stands on the stage, facing the person, not however it is turned mid-landing).
            float d = Vector3.Dot(facing * Vector3.right, Vector3.ProjectOnPlane(away, Vector3.up));
            float outer = Mathf.Abs(d) < .01f ? 0f : Mathf.Sign(d);
            if (role == "Creativity") yield return Magnify(character, body, outer);
            else if (role == "Decisions") yield return BrushStrokes(character, body, outer);
            else yield return TapeOut(character, body, outer);
            if (body.IntroActive) { body.IntroHands(null, null); body.IntroPose(); }
        }

        // ------------------------------------------------------------ Spark: a peek through the magnifier

        private IEnumerator Magnify(WindupCharacter character, WindupBody body, float outer)
        {
            float side = outer != 0f ? outer : ViewRightSide(body); // the outer side (else the person's right)
            bool rightHand = side > 0f;
            var tool = Tool("Intro Magnifier");
            // Built around the grip (the tool's origin), the lens up (+Y), its glass facing the person (+-Z).
            Piece(tool, Owned(ToyMeshes.Disc(.0036f, .026f, Wood, WoodDark, "MagnifierHandle")), new Vector3(0f, -.014f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            Piece(tool, Owned(ToyMeshes.Disc(.005f, .004f, Brass, BrassDark, "MagnifierCollar")), new Vector3(0f, .012f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            var ring = Piece(tool, Owned(Tinted(ToyMeshes.Torus(.17f, "MagnifierRing"), Brass)), new Vector3(0f, .0285f, 0f), Quaternion.identity);
            ring.localScale = Vector3.one * .0128f;
            Piece(tool, Owned(ToyMeshes.Disc(.012f, .0014f, Glass, Glass, "MagnifierGlass")), new Vector3(0f, .0285f, -.0007f), Quaternion.identity);
            Piece(tool, Owned(ToyMeshes.Disc(.0026f, .0004f, Color.white, Color.white, "MagnifierGlint")), new Vector3(-.0045f, .033f, -.0016f), Quaternion.identity);
            Vector3 lensFromGrip = new Vector3(0f, .0285f, 0f);
            // Held up outside the head on the outer side, the handle tipped inward to the mitten (Spark's head is 8 cm
            // wide at its eyes: beside the face meant inside the head, so the old search sank the lens to its feet).
            Quaternion toolTurn = Quaternion.Euler(0f, -6f * side, -LensTilt * side), handTurn = Quaternion.Euler(0f, 0f, -14f * side);
            var follow = Follow(tool, body, rightHand, toolTurn);
            Vector3 low = new Vector3(.034f * side, .058f, .03f);
            float duration = ToolDuration;
            ToolSound(tool, 1.5f);
            for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
            {
                if (Interrupted(character)) break;
                float up = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.1f, .32f, t)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.78f, .92f, t)));
                float sway = Mathf.Sin(Mathf.InverseLerp(.32f, .78f, t) * Mathf.PI * 2f) * .007f * up;
                // Every frame: right after the landing the squashed head is wider and lower than when it has settled.
                Vector3 eye = BesideFace(body, side, toolTurn * lensFromGrip);
                Vector3 lens = Vector3.Lerp(low + toolTurn * lensFromGrip, eye, up) + Vector3.right * sway;
                Vector3 grip = Reach(lens - toolTurn * lensFromGrip);
                var mitten = MittenAt(body, rightHand, grip, handTurn);
                if (rightHand) body.IntroHands(mitten, null); else body.IntroHands(null, mitten);
                body.IntroPose(lean: -2f * up);
                ScaleTool(tool, t);
                yield return null;
            }
            Drop(tool);
        }

        // (2026-11-13, the user: "Spark's magnifier overlaps its face") Beside the face on the side the person sees
        // on their right, lowered until, from the person's eyes (the camera), the lens no longer covers the face.
        // In the body's space, like EyeSpot.
        // `side`: the body's own right (+1) or left (-1); `lensFromGrip`: the lens from the mitten in the body's space.
        // The lens near eye height, out on that side as far as the mitten (ToolReach) and the tipped handle allow; then
        // outward and down a little at a time until, as the person sees it, it is off the face and out of the head.
        public const float LensTilt = 34f;
        public float TitleLensFaceGap { get; private set; }
        public float TitleLensNeeded { get; private set; }
        public float TitleLensHeight { get; private set; } // above the feet (m)
        private float lensMeasuredAt = -1f;
        private float TitleLensClear(Vector3 eyes, Vector3 face) => TitleLensNeeded = Mathf.Atan2(.015f, Mathf.Max(.05f, Vector3.Distance(eyes, face))) * Mathf.Rad2Deg;
        private Vector3 BesideFace(WindupBody body, float side, Vector3 lensFromGrip)
        {
            Transform b = body.transform;
            Vector3 forward = Vector3.ProjectOnPlane(b.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            var view = Camera.main;
            Vector3 eyes = view != null ? view.transform.position : b.position + forward * .6f + Vector3.up * .45f;
            float eyeHeight = Mathf.Max(.07f, body.HeadTop.y - b.position.y - .058f);
            // The middle of the face (its front at eye height; EyeSpot is the right eye, 1.1 cm toward the lens).
            Vector3 face = b.position + Vector3.up * eyeHeight + forward * .05f;
            for (float d = 0f; d < .12f; d += .003f)
                if (body.HeadDepth(b.position + Vector3.up * eyeHeight + forward * d) >= 1f) { face = b.position + Vector3.up * eyeHeight + forward * d; break; }
            const float ahead = .02f;
            float gripOut = Mathf.Sqrt(ToolReach * ToolReach - ahead * ahead) - .002f;
            Vector3 local = new Vector3(side * gripOut + lensFromGrip.x, eyeHeight - .006f, ahead + lensFromGrip.z);
            float gap = -99f;
            for (int i = 0; i < 12; i++)
            {
                Vector3 world = b.position + b.rotation * local;
                gap = Vector3.Angle(world - eyes, face - eyes)
                      - Mathf.Atan2(.03f, Mathf.Max(.01f, Vector3.Distance(eyes, face))) * Mathf.Rad2Deg
                      - Mathf.Atan2(.0135f, Mathf.Max(.01f, Vector3.Distance(eyes, world))) * Mathf.Rad2Deg;
                // Clear by 1.5 cm at the face's distance (a fixed 2 degrees asked 3.5 cm on the stage a metre away,
                // and the lens sank to the feet looking for it).
                if (gap >= TitleLensClear(eyes, face) && body.HeadDepth(world) >= 1.15f) break;
                local.y -= .004f;
            }
            // (checks) the lens at its highest: kept once it is up, after the landing has settled
            if (local.y >= TitleLensHeight || Time.time - lensMeasuredAt > .3f) { TitleLensFaceGap = gap; TitleLensHeight = local.y; }
            lensMeasuredAt = Time.time;
            return local;
        }

        // The body's side (+1 its right, -1 its left) that the person sees on their right.
        private static float ViewRightSide(WindupBody body)
        {
            Transform b = body.transform;
            Vector3 forward = Vector3.ProjectOnPlane(b.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            var view = Camera.main;
            Vector3 eyes = view != null ? view.transform.position : b.position + forward * .6f + Vector3.up * .45f;
            Vector3 viewRight = Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(b.position - eyes, Vector3.up).normalized);
            return Vector3.Dot(right, viewRight) >= 0f ? 1f : -1f;
        }

        // In front of the right eye (in the body's space): the face's surface at eye height, a little out from it.
        private Vector3 EyeSpot(WindupBody body)
        {
            Transform b = body.transform;
            Vector3 forward = Vector3.ProjectOnPlane(b.forward, Vector3.up).normalized;
            float eyeHeight = Mathf.Max(.07f, body.HeadTop.y - b.position.y - .058f);
            Vector3 from = b.position + Vector3.up * eyeHeight + b.right * .011f;
            Vector3 face = from + forward * .06f;
            for (float d = 0f; d < .12f; d += .003f)
            {
                Vector3 p = from + forward * d;
                if (body.HeadDepth(p) >= 1f) { face = p; break; }
            }
            Vector3 world = face + forward * .017f;
            return Quaternion.Inverse(b.rotation) * (world - b.position);
        }

        // ------------------------------------------------------------ Maker: two strokes with the big brush

        private IEnumerator BrushStrokes(WindupCharacter character, WindupBody body, float outer)
        {
            var tool = Tool("Intro Brush");
            Piece(tool, Owned(ToyMeshes.Disc(.0034f, .032f, MakerBlue, Color.Lerp(MakerBlue, Color.black, .25f), "BrushHandle")), new Vector3(0f, -.018f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            Piece(tool, Owned(ToyMeshes.Disc(.0044f, .008f, Steel, SteelDark, "BrushFerrule")), new Vector3(0f, .014f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            var hair = Piece(tool, Owned(ToyMeshes.Disc(.0046f, .011f, Bristle, Color.Lerp(Bristle, Color.black, .15f), "BrushBristles")), new Vector3(0f, .022f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            hair.localScale = new Vector3(1.3f, .7f, 1f);
            var wet = Piece(tool, Owned(ToyMeshes.Disc(.0034f, .006f, MakerBlue, MakerBlue, "BrushPaint")), new Vector3(0f, .032f, 0f), Quaternion.Euler(-90f, 0f, 0f));
            wet.localScale = new Vector3(1.3f, .7f, 1f);
            Vector3 tipLocal = new Vector3(0f, .039f, 0f);
            // Beside the face (in front of the body the brush covered the face); at an end of the stage on the outer
            // side, in the middle on its own right - the neighbour there is 14 cm away and holds its own tool outward.
            float side = outer != 0f ? outer : 1f;
            bool rightHand = side > 0f;
            const float across = .04f, lean = 32f, sweep = 30f;
            Quaternion handTurn = Quaternion.Euler(0f, 0f, -16f * side);
            Vector3 baseGrip = new Vector3(across * side, .064f, .034f), lowGrip = new Vector3(.032f * side, .05f, .026f);
            var follow = Follow(tool, body, rightHand, Quaternion.identity);
            float duration = ToolDuration, nextDab = 0f;
            ToolSound(tool, 1.25f);
            for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
            {
                if (Interrupted(character)) break;
                float lift = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.05f, .2f, t)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.82f, .94f, t)));
                float stroke = Mathf.InverseLerp(.22f, .8f, t);
                bool painting = stroke > 0f && stroke < 1f;
                // Two strokes: the tip sweeps out and back in front, leaning toward the person.
                float swing = painting ? Mathf.Sin(stroke * Mathf.PI * 2f) : 0f;
                Quaternion toolTurn = Quaternion.Euler(-24f * lift, 0f, side * (Mathf.Lerp(-8f, -lean, lift) + swing * sweep));
                follow.rotation = toolTurn;
                Vector3 grip = Reach(Vector3.Lerp(lowGrip, baseGrip, lift) + new Vector3(swing * .006f * side, Mathf.Abs(swing) * .004f, 0f));
                var mitten = MittenAt(body, rightHand, grip, handTurn);
                if (rightHand) body.IntroHands(mitten, null); else body.IntroHands(null, mitten);
                body.IntroPose(lean: -1.5f * lift);
                ScaleTool(tool, t);
                if (painting && Time.time >= nextDab)
                {
                    nextDab = Time.time + .028f;
                    StartCoroutine(Dab(tool.TransformPoint(tipLocal)));
                    if (Random.value < .15f) ToySounds.Play(ToySounds.Cue.Brush, tool.position, .25f, 1.5f);
                }
                yield return null;
            }
            Drop(tool);
        }

        // A dab of Maker's blue left in the air where the brush passed; it shrinks away.
        private IEnumerator Dab(Vector3 at)
        {
            var dab = Tool("Brush Dab");
            Piece(dab, DabMesh(), Vector3.zero, Quaternion.identity);
            dab.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at));
            float size = Random.Range(.8f, 1.2f);
            for (float t = 0f; t < 1f && dab != null; t += Time.deltaTime / .7f)
            {
                dab.localScale = Vector3.one * size * (t < .15f ? t / .15f : 1f - Mathf.SmoothStep(0f, 1f, (t - .15f) / .85f));
                yield return null;
            }
            if (dab != null) { tools.Remove(dab.gameObject); Destroy(dab.gameObject); }
        }

        private Mesh dabMesh;
        private Mesh DabMesh() => dabMesh != null ? dabMesh : (dabMesh = Owned(ToyMeshes.Disc(.0032f, .0012f, MakerBlue, MakerBlue, "BrushDab")));

        // ------------------------------------------------------------ Fixer: the tape measure

        private IEnumerator TapeOut(WindupCharacter character, WindupBody body, float outer)
        {
            // The case in the inner mitten (its round label toward the person), the tape's end pulled out by the
            // other mitten toward the outer side (it used to be pulled across, into the neighbour). `pull`: the
            // body's side the tape goes to (+1 its right).
            float pull = outer != 0f ? outer : 1f;
            bool pullRight = pull > 0f;
            var tool = Tool("Intro Tape Case");
            Piece(tool, Owned(ToyMeshes.RoundedSlab(.024f, .024f, .008f, .013f, TapeYellow, TapeDark, TapeYellow, "TapeCase")), new Vector3(0f, 0f, -.0065f), Quaternion.identity);
            Piece(tool, Owned(ToyMeshes.Disc(.0062f, .0012f, Ink, Ink, "TapeLabel")), new Vector3(0f, 0f, .0066f), Quaternion.identity);
            Piece(tool, Owned(ToyMeshes.Disc(.0062f, .0012f, Ink, Ink, "TapeLabelBack")), new Vector3(0f, 0f, -.0078f), Quaternion.identity);
            var slot = new GameObject("Slot").transform;
            slot.SetParent(tool, false);
            slot.localPosition = new Vector3(.012f * pull, -.0065f, 0f);
            var blade = Tool("Intro Tape");
            var strip = Piece(blade, Owned(ToyMeshes.RoundedSlab(1f, .0058f, .0004f, .0012f, TapeYellow, TapeDark, TapeYellow, "TapeBlade")), Vector3.zero, Quaternion.identity);
            var hook = Piece(blade, Owned(ToyMeshes.RoundedSlab(.0022f, .0085f, .0006f, .0032f, Ink, Ink, Ink, "TapeHook")), Vector3.zero, Quaternion.identity);
            var tape = blade.gameObject.AddComponent<IntroTapeBlade>();
            tape.slot = slot; tape.hand = body.RigHand(pullRight); tape.palm = PalmOf(body, pullRight); tape.strip = strip; tape.hook = hook;
            var follow = Follow(tool, body, !pullRight, Quaternion.identity);
            Quaternion caseTurn = Quaternion.Euler(0f, 0f, 14f * pull), pullTurn = Quaternion.Euler(0f, 0f, -14f * pull);
            Vector3 caseGrip = new Vector3(-.03f * pull, .06f, .034f);
            Vector3 pulledFrom = caseGrip + new Vector3(.02f * pull, -.006f, .002f), pulledTo = new Vector3(.05f * pull, .058f, .036f);
            float duration = ToolDuration;
            bool snapped = false;
            ToolSound(tool, 1.1f);
            for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
            {
                if (Interrupted(character)) break;
                float drawn = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.18f, .45f, t));
                bool held = t < .64f;
                if (!held && !snapped)
                {
                    snapped = true;
                    ToySounds.Play(ToySounds.Cue.Whoosh, slot.position, .45f, 2.3f);
                    ToySounds.Play(ToySounds.Cue.Press, slot.position, .35f, 2f);
                }
                tape.extend = held ? Mathf.InverseLerp(.15f, .22f, t) : Mathf.Max(0f, 1f - (t - .64f) / .05f);
                Vector3 end = held ? Vector3.Lerp(pulledFrom, pulledTo, drawn) : Vector3.Lerp(pulledTo, new Vector3(.03f * pull, .052f, .028f), Mathf.InverseLerp(.64f, .74f, t));
                var endMitten = MittenAt(body, pullRight, Reach(end), pullTurn);
                var caseMitten = MittenAt(body, !pullRight, Reach(caseGrip), caseTurn);
                if (pullRight) body.IntroHands(endMitten, caseMitten); else body.IntroHands(caseMitten, endMitten);
                // The nod: "that fits".
                float nod = Mathf.Sin(Mathf.Clamp01(Mathf.InverseLerp(.7f, .9f, t)) * Mathf.PI);
                body.IntroPose(nod: 7f * nod * nod);
                ScaleTool(tool, t);
                yield return null;
            }
            tape.extend = 0f;
            Drop(blade);
            Drop(tool);
        }

        // ------------------------------------------------------------ shared

        // A mitten whose palm is at `grip` (body space, metres), turned `turn` (body space): the hand bone's pose
        // for WindupBody.IntroHands.
        private Pose MittenAt(WindupBody body, bool right, Vector3 grip, Quaternion turn)
        {
            var hand = body.RigHand(right);
            Transform b = body.transform;
            Quaternion worldTurn = b.rotation * turn;
            Vector3 gripWorld = b.position + b.rotation * grip;
            Vector3 wrist = gripWorld - worldTurn * Vector3.Scale(PalmOf(body, right), hand.lossyScale);
            ToolReachMax = Mathf.Max(ToolReachMax, Vector3.ProjectOnPlane(wrist - b.position, Vector3.up).magnitude);
            return new Pose(b.InverseTransformPoint(wrist), turn);
        }

        // Within ToolReach of the body axis (horizontally).
        private static Vector3 Reach(Vector3 local)
        {
            var flat = new Vector2(local.x, local.z);
            if (flat.magnitude > ToolReach) flat = flat.normalized * ToolReach;
            return new Vector3(flat.x, local.y, flat.y);
        }

        // The middle of a mitten in its hand bone's space, measured once from the mitten mesh.
        private Vector3 PalmOf(WindupBody body, bool right)
        {
            if (palms.TryGetValue((body, right), out var palm)) return palm;
            palm = Vector3.zero;
            var hand = body.RigHand(right);
            string name = right ? "Hand_R" : "Hand_L";
            foreach (var skin in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin.name != name || hand == null) continue;
                var mesh = new Mesh();
                skin.BakeMesh(mesh, true);
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                foreach (var v in mesh.vertices)
                {
                    var p = hand.InverseTransformPoint(skin.transform.TransformPoint(v));
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                if (mesh.vertexCount > 0) palm = (min + max) * .5f;
                Destroy(mesh);
                break;
            }
            palms[(body, right)] = palm;
            return palm;
        }

        private NotebookPropFollower Follow(Transform tool, WindupBody body, bool right, Quaternion turn)
        {
            var follow = tool.gameObject.AddComponent<NotebookPropFollower>();
            follow.hand = body.RigHand(right);
            follow.handLocal = PalmOf(body, right);
            follow.rotationFrame = body.transform;
            follow.rotation = turn;
            follow.gripLocal = Vector3.zero;
            return follow;
        }

        private Transform Tool(string name)
        {
            var root = new GameObject(name).transform;
            root.SetParent(transform, false);
            root.localScale = Vector3.zero;
            tools.Add(root.gameObject);
            return root;
        }

        private Transform Piece(Transform parent, Mesh mesh, Vector3 local, Quaternion turn)
        {
            var t = Part(mesh.name, mesh, parent);
            t.localPosition = local;
            t.localRotation = turn;
            return t;
        }

        private Mesh Owned(Mesh mesh) { owned.Add(mesh); return mesh; }

        private static Mesh Tinted(Mesh mesh, Color colour)
        {
            var colours = new Color[mesh.vertexCount];
            for (int i = 0; i < colours.Length; i++) colours[i] = colour;
            mesh.colors = colours;
            return mesh;
        }

        // Pops in (a springy overshoot) at the start, shrinks away at the end.
        private static void ScaleTool(Transform tool, float t)
        {
            if (tool == null) return;
            float s = t < .12f ? Overshoot(t / .12f) : t > .9f ? 1f - Mathf.SmoothStep(0f, 1f, (t - .9f) / .1f) : 1f;
            tool.localScale = Vector3.one * Mathf.Max(.0001f, s);
        }

        private void ToolSound(Transform tool, float pitch)
        {
            StartCoroutine(PopSoon(tool, pitch));
        }

        private IEnumerator PopSoon(Transform tool, float pitch)
        {
            yield return null; // the follower has placed it in the mitten
            if (tool == null) yield break;
            ToySounds.Play(ToySounds.Cue.Pop, tool.position, .4f, pitch);
            ToyFx.Puff(tool.position, .006f, 2);
        }

        private void Drop(Transform tool)
        {
            if (tool == null) return;
            if (tool.localScale.x > .2f) ToyFx.Puff(tool.position, .006f, 2);
            tools.Remove(tool.gameObject);
            Destroy(tool.gameObject);
        }

        private void ClearTools()
        {
            foreach (var go in tools) if (go != null) Destroy(go);
            tools.Clear();
        }
    }

    // The tape between the case's slot and the right mitten (after the hand pose, like the tools), its face toward
    // the person; `extend` pulls it in toward the slot (0 = all in).
    [DefaultExecutionOrder(320)]
    public sealed class IntroTapeBlade : MonoBehaviour
    {
        public Transform slot, hand, strip, hook;
        public Vector3 palm;
        public float extend;

        private void LateUpdate()
        {
            if (slot == null || hand == null || strip == null) return;
            Vector3 a = slot.position, b = hand.TransformPoint(palm);
            Vector3 d = b - a;
            float length = d.magnitude * Mathf.Clamp01(extend);
            bool shown = length > .003f && slot.lossyScale.x > .2f;
            if (strip.gameObject.activeSelf != shown) { strip.gameObject.SetActive(shown); hook.gameObject.SetActive(shown); }
            if (!shown) return;
            Vector3 x = d.normalized;
            var eye = Camera.main;
            Vector3 toEye = eye != null ? eye.transform.position - a : -transform.forward;
            Vector3 z = -Vector3.ProjectOnPlane(toEye, x).normalized;
            if (z.sqrMagnitude < .5f) z = Vector3.Cross(x, Vector3.up).normalized;
            Quaternion turn = Quaternion.LookRotation(z, Vector3.Cross(z, x));
            transform.localScale = Vector3.one;
            strip.SetPositionAndRotation(a + x * length * .5f, turn);
            strip.localScale = new Vector3(length, 1f, 1f);
            hook.SetPositionAndRotation(a + x * length, turn);
        }
    }
}
