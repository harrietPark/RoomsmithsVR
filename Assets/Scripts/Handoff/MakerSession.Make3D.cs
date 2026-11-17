using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Meta.XR.MRUtilityKit;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    // F29 phase 3: making.
    //  - Two kinds, both built by the app's own code to the measured size: coasters (round, hex,
    //    octagon, square, flower or the person's drawn outline) and vessels (pen holders, cups,
    //    planters, vases, lamp shades: a side profile turned around its axis; straight, tapered,
    //    curvy, bowl or the person's drawn side).
    //  - After a make, each teammate plants a flag on a part of the object with a change (a thumb notch
    //    on the rim, a thicker base...). Pinch a flag: the change is planned and the next version is
    //    built right away (no preview, no "make it", since 2026-11-11) with the same paint; earlier
    //    versions step aside on the desk with v1, v2 tags, so they can be compared side by side (and
    //    voted on). A spoken change rebuilds the made thing in place instead ("new version" keeps both).
    //  - Everything made stays where it was put, at the real surface's anchor, and all of it comes back
    //    next time (the room fills up with things the person made).
    //  - Moodboard: looks that were not picked (and anything the person says "pin") go up on the nearest
    //    real wall, anchored there, and stay between sessions. Pinch one to make the next thing like it.
    public sealed partial class MakerSession
    {
        public sealed class Version { public int number; public Transform root; public CoasterDesign design; public Transform tag; }
        // F72: turn (world rotation) and scale for a picture put down in the air or on the idea wall's board.
        [Serializable] public sealed class Pin { public string image, label, anchor, at; public float[] local, rotation, world, turn; public float scale; }
        [Serializable] private sealed class MoodboardData { public List<Pin> pins = new List<Pin>(); }

        public readonly List<Version> Versions = new List<Version>();
        public readonly List<Transform> RoomItems = new List<Transform>();
        public readonly List<Pin> Pins = new List<Pin>();
        public readonly List<Transform> PinViews = new List<Transform>();
        public int MadeCount { get; private set; }
        // F33 (2026-11-11): every object is made by Meshy from the chosen picture, whatever it is. The
        // F30 kinds (coaster, vessel, tray, clip: shapes the app built itself) are no longer chosen; their
        // code stays for things made before, which still load and print.
        // F41 (2026-11-12): except a desk clip (clip, hook, clamp, cable... in the words or the picked idea): its
        // gap must fit the real desk to a fraction of a millimetre, so the app builds it (FitShapes.Clip).
        // F49: new makes follow the chosen clay picture, including hooks/clips; legacy saved fit shapes still load.
        public string Kind => !FormFirst && (KindOverride == "clip" || (KindOverride == null && KindFor(Need) == "clip")) ? "clip" : "model";
        public static string MoodboardPath => Path.Combine(Application.persistentDataPath, "maker_moodboard.json");
        private static string MoodboardFolder => Path.Combine(Application.persistentDataPath, "moodboard");

        private bool productFinished, pinsLoaded;
        private Vector3 wallPoint, wallNormal;
        private MRUKAnchor wallAnchor;
        private bool hasWall;

        // ------------------------------------------------------------ vessel plans (no AI: words, drawing and hands)

        private string VesselWord()
        {
            string w = (Need + " " + TargetName).ToLowerInvariant();
            return w.Contains("pen") ? "pen holder" : w.Contains("vase") || w.Contains("꽃병") ? "vase" : w.Contains("planter") || w.Contains("pot") || w.Contains("화분") ? "planter"
                : w.Contains("lamp") || w.Contains("shade") || w.Contains("조명") ? "lamp shade" : w.Contains("bowl") || w.Contains("그릇") ? "bowl"
                : w.Contains("cup") || w.Contains("mug") || w.Contains("tumbler") || w.Contains("컵") ? "cup" : "holder";
        }

        private float DefaultHeight()
        {
            switch (VesselWord())
            {
                case "pen holder": return 100f;
                case "vase": return 160f;
                case "planter": return Mathf.Max(80f, Measured * .9f);
                case "lamp shade": return 140f;
                case "bowl": return 60f;
                case "cup": return 95f;
                default: return Mathf.Max(70f, Measured * 1.1f);
            }
        }

        private IEnumerator VesselPlan(string change)
        {
            Current = Step.Planning;
            Hush();
            Ack(Crew.Maker, change.Length > 0 ? "On it!" : "Ooh, nice.");
            yield return new WaitForSeconds(Scripted ? .3f : .8f);
            string w = (Shape + " " + change).ToLowerInvariant();
            var d = Design != null && Design.IsVessel ? Design.Copy() : new CoasterDesign { kind = "vessel", clearance = 4f, rimWidth = 2.4f, baseThickness = 3f, ribs = 0, profile = "straight" };
            d.kind = "vessel";
            d.cupDiameter = Measured;
            float height = Design != null && Design.IsVessel ? Design.Height : DefaultHeight();
            if (DrawnProfile != null && (w.Contains("drawn") || w.Contains("own") || w.Contains("그린"))) { d.profile = "drawn"; d.sideProfile = DrawnProfile; height = Mathf.Clamp(DrawnAspect * (Measured + 2f * d.rimWidth), 30f, 300f); }
            if (w.Contains("taper") || w.Contains("cone") || w.Contains("좁")) d.profile = "tapered";
            else if (w.Contains("curv") || w.Contains("belly") || w.Contains("vase") || w.Contains("곡")) d.profile = "curvy";
            else if (w.Contains("bowl") || w.Contains("wide open") || w.Contains("넓")) d.profile = "bowl";
            else if (w.Contains("straight") || w.Contains("simple") || w.Contains("cylinder") || w.Contains("곧")) d.profile = "straight";
            var size = Regex.Match(w, @"(\d+(?:\.\d+)?)\s*(mm|cm)");
            if (size.Success)
            {
                float mm = float.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * (size.Groups[2].Value == "cm" ? 10f : 1f);
                if (w.Contains("tall") || w.Contains("high") || w.Contains("height") || w.Contains("size") || w.Contains("높")) height = mm;
            }
            else if (HandSizeMm > 20f && Design == null) height = HandSizeMm;
            if (w.Contains("taller") || w.Contains("higher") || w.Contains("더 높")) height *= 1.2f;
            if (w.Contains("shorter") || w.Contains("lower") || w.Contains("더 낮")) height *= .83f;
            if (w.Contains("thicker") || w.Contains("sturd") || w.Contains("두껍")) { d.rimWidth += .8f; d.baseThickness += 1f; }
            if (w.Contains("thinner") || w.Contains("얇")) d.rimWidth -= .4f;
            if (w.Contains("roomier") || w.Contains("looser") || w.Contains("bigger inside")) d.clearance += 3f;
            d.rimHeight = Mathf.Max(10f, height - d.baseThickness);
            d.name = Look != null ? MakerRoles.Clean(Look.label) : Capital(VesselWord());
            if (string.IsNullOrEmpty(d.color)) d.color = Palette.LastOrDefault(s => s.kind == "colour")?.hex ?? "#e8d2a8";
            d.why = "";
            Design = d.Clamped();
            Note("design", Design.name, Design.Summary);
            Say(Crew.Maker, $"A {Design.profile} {VesselWord()}, {Design.Height:0} millimetres tall. Making it!");
            yield return FitAndBuild();
        }

        // ------------------------------------------------------------ versions

        // A finished make steps aside (with its version tag) when the next version is built. F40: made from the
        // draft, the next version goes where the draft stood and this one stays where it is (`slide` false).
        private void ArchiveProduct(bool slide = true)
        {
            if (Product == null) return;
            if (!productFinished) { Destroy(Product.gameObject); Product = null; return; }
            var old = Product;
            Product = null;
            productFinished = false;
            var version = Versions.FirstOrDefault(v => v.root == old);
            if (version == null)
            {
                version = new Version { number = Versions.Count + 1, root = old, design = (archiveDesign ?? Design)?.Copy() };
                Versions.Add(version);
            }
            archiveDesign = null;
            if (!slide)
            {
                version.tag = VersionTag(old, "v" + version.number);
                Talk.Add("v" + version.number + " stays where it is: " + version.design?.Summary);
                return;
            }
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - MugSpot, Vector3.up).normalized : Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, toUser);
            float step = Mathf.Max(.09f, FitShapes.Footprint(version.design) * .001f + .03f);
            bool onEdge = version.design?.kind == "clip" && HasEdge;
            if (onEdge) side = EdgeAlong; // F30: a clip steps aside along its desk edge
            Vector3 aside = MugSpot - side * step * Versions.Count(v => v.root != null);
            Vector3 onDesk = onEdge ? aside : WorkSurface != null ? WorkPoint(aside, .04f) : new Vector3(aside.x, DeskHeightAt(aside, MugSpot.y), aside.z);
            if (onEdge) rails[old] = (onDesk, EdgeAlong, old.rotation);
            var body = old.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = true;
            StartCoroutine(Slide(old, onDesk));
            version.tag = VersionTag(old, "v" + version.number);
            Talk.Add("v" + version.number + " stepped aside: " + version.design?.Summary);
        }

        private IEnumerator Slide(Transform target, Vector3 to)
        {
            Vector3 from = target.position;
            for (float t = 0f; t < 1f && target != null; t += Time.deltaTime / .5f)
            {
                target.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t)) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .03f;
                yield return null;
            }
            if (target != null) target.position = to;
        }

        private Transform VersionTag(Transform on, string label)
        {
            var existing = on.Find("Version Tag");
            if (existing != null) Destroy(existing.gameObject);
            var tag = PaperTag("Version Tag", .036f, .02f, new Color(1f, .97f, .85f), out var text, 11f); // F77: a paper tag, not a pill
            text.text = label;
            tag.name = "Version Tag";
            tag.SetParent(on, true);
            tag.position = on.position + Vector3.up * .06f;
            return tag;
        }

        // After a make: the made thing gets its tag (when there are versions), its see-through twin (v2, the
        // draft: MakerSession.Draft.cs) appears beside it, and the crew offers its flags for it.
        // (2026-11-13, the user: "they keep looking down then up") The crew used to look at the made thing on the desk
        // whenever there was one, and up at the person for every line: a nod each time. They look at it only for a
        // while after it is made (or while it is grabbed), and at the person otherwise.
        public static float ProductLookSeconds = 6f;
        private float productLookUntil;
        public bool CrewWatchingProduct => Product != null && (Time.time < productLookUntil || held == Product);

        private void AfterMake()
        {
            productLookUntil = Time.time + ProductLookSeconds;
            MadeCount++;
            productFinished = true;
            OfferFeaturesLater(); // F59: a lamp's light, a planter's drain, a vessel's hollow
            // (A version rebuilt in place keeps its number: Build gives its entry the new thing.)
            if (Versions.Count > 0 && Product != null && Versions.All(v => v.root != Product))
            {
                Versions.Add(new Version { number = Versions.Count + 1, root = Product, design = Design.Copy() });
                Versions[Versions.Count - 1].tag = VersionTag(Product, "v" + Versions.Count);
            }
            // F49: the white clay make goes to the finish lesson first; v2 and the flags come after it (EndFinish).
            if (FormFirst) return; // F50: explicit review owns drafts; finishing never silently starts another version.
            MakeDraft();
            StartCoroutine(FlagsLater());
        }

        private IEnumerator FlagsLater()
        {
            float until = Time.time + 12f;
            yield return new WaitForSeconds(1.5f);
            while (Time.time < until && (crewBusy || lines.Count > 0 || Voice.IsSpeaking || Voice.Loading)) yield return null;
            for (float wait = Time.time + 18f; crewBusy && Time.time < wait; ) yield return null; // a turn in progress finishes first
            Debug.Log("[Windups Draft] made turn: step " + Current + ", made " + (Product != null) + ", crew busy " + crewBusy);
            if (Current == Step.Done && Product != null) yield return CrewTurn("made", "");
            if (Current == Step.Done && Draft != null) ExplainDraft(); // F40: every time, after the flags
        }

        // ------------------------------------------------------------ the room remembers everything made

        // F91: adds the saved makes not back yet (those already here stay as they are, wherever they were moved).
        private void RestoreRoom(List<Made> items)
        {
            foreach (var item in items)
            {
                if (item?.design == null || restoredMade.Contains(RestoreKey(item))) continue;
                if (!RestorePlace(item, out Vector3 at, out Quaternion rot, out bool byWorld)) continue; // the welcome tries again
                restoredMade.Add(RestoreKey(item));
                var design = item.design.Clamped();
                var mesh = CoasterMesh.Build(design);
                Paint(mesh, design.Tint(new Color(.95f, .85f, .62f)));
                var made = Solid(mesh, "Kept " + design.name, at, out _, design);
                made.rotation = rot;
                if (byWorld) ClearOfRoom(made);
                AfterSolid(made, design);
                madeNames[made] = item.name;
                TrackRestoredWork(item, made);
                if (!string.IsNullOrEmpty(item.notebookId)) notebookOwners[made] = item.notebookId;
                ApplyMarks(made, item.paint);
                RoomItems.Add(made);
                RestoreStand(made, item.stand, item.standAt, item.standTurn); // F52: back on its display stand (F104: where it was)
            }
        }

        // ------------------------------------------------------------ moodboard on a real wall

        private void LoadPins()
        {
            if (pinsLoaded) return;
            pinsLoaded = true;
            try { if (File.Exists(MoodboardPath)) Pins.AddRange((JsonConvert.DeserializeObject<MoodboardData>(File.ReadAllText(MoodboardPath)) ?? new MoodboardData()).pins); }
            catch (Exception exception) { Debug.LogWarning("[Windups Moodboard] " + exception.Message); }
        }

        public static void ForgetMoodboard()
        {
            try { if (File.Exists(MoodboardPath)) File.Delete(MoodboardPath); } catch { }
            try { if (Directory.Exists(MoodboardFolder)) Directory.Delete(MoodboardFolder, true); } catch { }
        }

        // The nearest real wall roughly in front of the person (MRUK geometry), at eye height.
        private bool FindWall()
        {
            if (hasWall) return true;
            if (Head == null) return false;
            var room = arrival.handoff.meeting.environment;
            Vector3 forward = Vector3.ProjectOnPlane(Head.forward, Vector3.up).normalized;
            float best = float.MaxValue;
            foreach (float turn in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f })
            {
                var ray = new Ray(Head.position - Vector3.up * .05f, Quaternion.Euler(0f, turn, 0f) * forward);
                if (!Physics.Raycast(ray, out var hit, 4f, room.GeometryMask, QueryTriggerInteraction.Ignore) || Mathf.Abs(hit.normal.y) > .3f) continue;
                float score = hit.distance + Mathf.Abs(turn) * .01f;
                if (score >= best) continue;
                best = score;
                wallPoint = hit.point;
                wallNormal = Vector3.ProjectOnPlane(hit.normal, Vector3.up).normalized;
                wallAnchor = hit.collider.GetComponentInParent<MRUKAnchor>();
            }
            hasWall = best < float.MaxValue;
            return hasWall;
        }

        private Vector3 PinSlot(int i)
        {
            Vector3 right = Vector3.Cross(Vector3.up, wallNormal);
            int col = i % 4, row = i / 4;
            return wallPoint + wallNormal * .008f + right * (col - 1.5f) * .13f + Vector3.up * (.12f - row * .14f);
        }

        // Pin a picture on the moodboard (it flies from where it is).
        private void PinUp(byte[] jpeg, string label, Vector3 from)
        {
            if (jpeg != null && NoteOnWall && board != null) { PinUpOnBoard(jpeg, label, from); return; } // F72: on the idea wall's board
            if (jpeg == null || !FindWall()) return;
            LoadPins();
            string path = null;
            try
            {
                Directory.CreateDirectory(MoodboardFolder);
                path = Path.Combine(MoodboardFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".jpg");
                File.WriteAllBytes(path, jpeg);
            }
            catch (Exception exception) { Debug.LogWarning("[Windups Moodboard] " + exception.Message); return; }
            Vector3 slot = PinSlot(Pins.Count);
            Quaternion facing = Quaternion.LookRotation(-wallNormal, Vector3.up);
            var pin = new Pin { image = path, label = MakerRoles.Clean(label), anchor = wallAnchor != null ? AnchorKey(wallAnchor) : "", at = DateTime.Now.ToString("MMM d") };
            pin.world = new[] { slot.x, slot.y, slot.z };
            var rot = facing;
            if (wallAnchor != null)
            {
                Vector3 local = wallAnchor.transform.InverseTransformPoint(slot);
                Quaternion lr = Quaternion.Inverse(wallAnchor.transform.rotation) * facing;
                pin.local = new[] { local.x, local.y, local.z };
                pin.rotation = new[] { lr.x, lr.y, lr.z, lr.w };
            }
            Pins.Add(pin);
            try { File.WriteAllText(MoodboardPath, JsonConvert.SerializeObject(new MoodboardData { pins = Pins }, Formatting.Indented)); } catch { }
            var view = PinView(pin);
            view.SetPositionAndRotation(from, facing);
            StartCoroutine(FlyPin(view, slot, rot));
            Talk.Add("Pinned to the moodboard: " + pin.label);
        }

        private IEnumerator FlyPin(Transform view, Vector3 to, Quaternion rot)
        {
            Vector3 from = view.position;
            for (float t = 0f; t < 1f && view != null; t += Time.deltaTime / .7f)
            {
                view.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t)) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .15f;
                view.rotation = Quaternion.Slerp(view.rotation, rot, t);
                yield return null;
            }
            if (view == null) yield break;
            view.SetPositionAndRotation(to, rot);
            ToySounds.Play(ToySounds.Cue.Press, to, .45f, 1.8f);
        }

        public const float PushpinScale = 1.4f; // the model's 8 mm head at 11 mm on an 11 cm picture

        private Transform PinView(Pin pin)
        {
            var root = new GameObject("Pin " + pin.label).transform;
            root.SetParent(transform, false);
            var card = ToyMeshes.RoundedSlab(.11f, .125f, .004f, .002f, Color.white, new Color(.85f, .85f, .88f), Color.white, "PinCard");
            owned.Add(card);
            Part(root, "Card", card, new Vector3(0f, -.006f, 0f), 0f);
            // 2026-11-15: the designed pushpin (Resources/WindupsPin, needle into the card); the flat disc without it.
            var pinModel = Resources.Load<GameObject>("WindupsPin");
            if (pinModel != null)
            {
                var pushpin = Instantiate(pinModel, root, false).transform;
                pushpin.name = "Pushpin";
                pushpin.localPosition = new Vector3(0f, .05f, -.0026f);
                pushpin.localRotation = Quaternion.identity;
                pushpin.localScale = Vector3.one * PushpinScale;
            }
            else Part(root, "Pushpin", StickerMesh("pushpin", () => ToyMeshes.Disc(.006f, .004f, new Color(.95f, .35f, .3f), new Color(.75f, .25f, .2f), "Pushpin")), new Vector3(0f, .05f, -.004f), 0f);
            var canvas = new GameObject("Picture", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(root, false);
            rect.sizeDelta = new Vector2(100f, 100f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(0f, .0f, -.0025f);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var image = new GameObject("Image", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            image.rectTransform.SetParent(rect, false);
            image.rectTransform.anchorMin = Vector2.zero;
            image.rectTransform.anchorMax = Vector2.one;
            image.rectTransform.offsetMin = image.rectTransform.offsetMax = Vector2.zero;
            image.raycastTarget = false;
            image.texture = File.Exists(pin.image) ? LoadSwatchTexture(pin.image) : null;
            PinViews.Add(root);
            return root;
        }

        // Last time's moodboard comes back on its wall.
        private void RestorePins()
        {
            LoadPins();
            if (PinViews.Count > 0) return;
            foreach (var pin in Pins)
            {
                Vector3 at = pin.world != null && pin.world.Length == 3 ? new Vector3(pin.world[0], pin.world[1], pin.world[2]) : Vector3.zero;
                Quaternion rot = Quaternion.identity;
                var anchor = FindAnchor(pin.anchor);
                if (anchor != null && pin.local != null && pin.rotation != null)
                {
                    at = anchor.transform.TransformPoint(new Vector3(pin.local[0], pin.local[1], pin.local[2]));
                    rot = anchor.transform.rotation * new Quaternion(pin.rotation[0], pin.rotation[1], pin.rotation[2], pin.rotation[3]);
                }
                else if (pin.turn != null && pin.turn.Length == 4) rot = new Quaternion(pin.turn[0], pin.turn[1], pin.turn[2], pin.turn[3]); // F72
                else if (FindWall()) rot = Quaternion.LookRotation(-wallNormal, Vector3.up);
                var view = PinView(pin);
                view.SetPositionAndRotation(at, rot);
                if (pin.scale > 0f) view.localScale = Vector3.one * pin.scale;
            }
        }

        private Pin PinNear(Vector3 point, Ray? ray, out Transform view)
        {
            view = null;
            for (int i = 0; i < PinViews.Count && i < Pins.Count; i++)
            {
                var v = PinViews[i];
                if (v == null) continue;
                bool hit = Vector3.Distance(point, v.position) < .07f;
                if (!hit && ray.HasValue && new Plane(v.forward, v.position).Raycast(ray.Value, out float d) && d > 0f)
                {
                    Vector3 l = v.InverseTransformPoint(ray.Value.GetPoint(d));
                    hit = Mathf.Abs(l.x) < .06f && Mathf.Abs(l.y) < .07f;
                }
                if (hit) { view = v; return Pins[i]; }
            }
            return null;
        }

        // A pinned picture is a reference for the next thing.
        private void UsePin(Pin pin, Transform view)
        {
            if (pin == null || !File.Exists(pin.image)) return;
            Heard();
            StartCoroutine(Bump(view));
            Talk.Add("Person picked a moodboard picture: " + pin.label);
            // F33: while the shape or the colours are being given, a saved picture is one of those inputs.
            if (Current == Step.ShapeInput || Current == Step.ColourInput) { InputPicture(File.ReadAllBytes(pin.image), pin.label); return; }
            Reference = File.ReadAllBytes(pin.image);
            if (Current == Step.Suggesting && string.IsNullOrEmpty(Need))
            {
                Need = "Something like my moodboard picture: " + pin.label;
                arrival.WriteTopic("Like my " + MakerRoles.Clean(pin.label)); // after a quiet start: the waiting note goes into the ring
                Note("brief", "What we're making", Need);
                StartShapeInput();
                InputPicture(File.ReadAllBytes(pin.image), pin.label);
                return;
            }
            if (Current == Step.Style || Current == Step.Looks || Current == Step.Suggesting)
            {
                if (Current == Step.Suggesting && string.IsNullOrEmpty(Need)) { Need = "Something like my moodboard picture: " + pin.label; Note("brief", "What we're making", Need); }
                string ask = "make it like this saved look: " + pin.label;
                Current = Current == Step.Suggesting ? Step.Style : Current;
                StepInput(ask, ask.ToLowerInvariant());
            }
            else Say(Crew.Spark, "That's " + pin.label + " from your wall. Want the next one like it?");
        }

        private bool Make3DVoice(string p)
        {
            if (p.Contains("pin it") || p.Contains("pin them") || p.Contains("on the wall") || p.Contains("moodboard") || p.Contains("벽에") || p.Contains("붙여"))
            {
                var pics = ShownLooks();
                if (pics.Count == 0 && Look != null) pics = Looks.Where(l => l.root != null && l.look == Look).ToList();
                if (pics.Count == 0) { Say(Crew.Spark, "Nothing to pin yet!"); return true; }
                foreach (var pic in pics) PinUp(pic.jpeg, pic.look?.label ?? "Look", pic.root.position);
                Ack(Crew.Spark, hasWall || (NoteOnWall && board != null) ? "On the wall!" : "I can't find a wall.");
                return true;
            }
            return false;
        }
    }
}
