using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Windups.Handoff;

namespace Windups
{
    // F26 "make what your space needs": a small maker crew (Six Hats personalities, workshop jobs).
    //   Spark (Creativity, green)  curious explorer: roams the room suggesting things to make, grabs
    //                              colours and textures from real objects and screens, remembers what
    //                              was made and where it lives.
    //   Maker (Decisions, blue)    calm, quick craftsman and the team's lead: runs the flow, sketches
    //                              three looks, builds the object in place, prints it.
    //   Fixer (Caution, black)     careful, a little dry: checks the fit (gap, thickness, heat), fixes
    //                              the plan before it is built and says how the made thing fits.
    // The person measures (hand tape), chooses, places and says "print". Since 2026-11-11 a design is built
    // as soon as it is planned (no see-through preview, no "make it").
    public enum Crew { Spark, Maker, Fixer }

    [Serializable] public sealed class MakerLook { public string label; public string prompt; }
    [Serializable] public sealed class LooksReply { public string say; public MakerLook[] looks; public string seen; public SizeAsk[] sizes; } // F60: seen (forms only); F61: sizes (forms, looks)
    // F61: one size the measuring step asks (W width, D depth, H height of the whole object).
    [Serializable] public sealed class SizeAsk { public string axis; public string label; public string why; public float typical; public bool must; }
    [Serializable] public sealed class DesignReply { public string say; public CoasterDesign design; }
    [Serializable] public sealed class NoteReply { public string say; }
    [Serializable] public sealed class MaterialReply { public string say; public string material; }
    // F30: who wrote it (each teammate sticks its own note) and what kind of thing the crew would make.
    [Serializable] public sealed class MakerIdea { public string who; public string label; public string forObject; public string kind; public string why; public string need; }
    [Serializable] public sealed class IdeasReply { public string say; public MakerIdea[] ideas; }
    // F29: what the crew does next (the AI picks actions; the app carries them out in the room).
    // F47: tag = the sticker's few words, shown on it (the full thought stays in 'say').
    [Serializable] public sealed class CrewAction { public string who; public string act; public int look; public string mood; public string part; public string say; public string tag; public string[] pros; public string[] cons; public string[] options; }
    [Serializable] public sealed class TurnReply { public CrewAction[] actions; }

    public static class MakerRoles
    {
        public static readonly string[] Names = { "Spark", "Maker", "Fixer" };
        public static readonly string[] Characters = { "Creativity", "Decisions", "Caution" };
        public static readonly Color[] Colors = { new Color(.2f, .72f, .36f), new Color(.2f, .48f, .95f), new Color(.24f, .24f, .3f) };
        public static string Name(Crew c) => Names[(int)c];
        public static Color Color(Crew c) => Colors[(int)c];
        public static string CharacterFor(Crew c) => Characters[(int)c];
        public static bool InCast(string character) => Array.IndexOf(Characters, character) >= 0;
        public static bool TryForCharacter(string character, out Crew crew)
        {
            int i = Array.IndexOf(Characters, character);
            crew = i < 0 ? Crew.Maker : (Crew)i;
            return i >= 0;
        }
        // Voices: Spark bright (coral), Maker warm and steady (sage), Fixer dry and short (ash).
        public static Role VoiceOf(Crew c) => c == Crew.Spark ? Role.Dreamer : c == Crew.Maker ? Role.Realist : Role.Critic;

        // Intro cards (screen text, English only).
        public static string Intro(Crew c) => c == Crew.Spark ? "I spot ideas around you." : c == Crew.Maker ? "I sketch it and build it." : "I make sure it fits.";
        // 2026-11-13: the intro cards name each teammate's tools and what they are for (MakerSession.IntroTools.cs).
        public static string Profile(Crew c) => c == Crew.Spark ? "Finds ideas & materials" : c == Crew.Maker ? "Turns ideas into things" : "Makes it work";
        public static string[] Jobs(Crew c) =>
            c == Crew.Spark ? new[] { "Magnifier: room ideas", "Palette: colours, textures", "Frame: real shapes" }
            : c == Crew.Maker ? new[] { "Sketchbook: draw shapes", "Notebook: all we decided", "Makes it in 3D", "Sends it to your printer" }
            : new[] { "Tape: measures real stuff", "Checks fit and printing", "Adds holes and hollows", "Gear: your settings" };

        // Quality-first model (the person waits while Maker draws; the cards only appear when done).
        public const string ImageModel = "gpt-image-2.6-sunburst", ImageQuality = "medium";
        public const string ImageStyle = " Photorealistic studio product photo of exactly one object, centred with the whole object in frame and some margin, three-quarter view from slightly above, sharp focus, true-to-life material and surface texture, soft even studio light with a gentle contact shadow, seamless plain light grey background. No text, no logos, no hands, no people, no other objects.";
        // F49: the shape sketches before measuring: the form only, as a designer's white clay model (Meshy reads the
        // shape best from a plain one; colours come after the make).
        public const string ClayStyle = " An unpainted matte white clay model of exactly one object, like a product designer's clay maquette: one plain warm white all over, no paint, no colours, no pattern, no printed texture, no decoration colour, the form shown by soft shading only, plain smooth surfaces unless the prompt asks for a texture or relief. Centred with the whole object in frame and some margin, three-quarter view from slightly above, soft even studio light with a gentle contact shadow, seamless plain light grey background. No text, no logos, no hands, no people, no other objects.";
        // What the crew can make in this version (F30: the app builds the first four kinds itself; any other
        // object comes from a picture through Meshy, when it is set up).
        public const string Makeable = "coaster: a coaster or cup holder for a cup, mug, glass, can or bottle; " +
                                       "vessel: a pen holder, cup, planter, vase, bowl or lamp shade; " +
                                       "tray: a tray with pockets shaped like the things laid on the desk (keys, phone, earbuds, glasses, a watch); " +
                                       "clip: a hook, cable clip or little shelf that clamps onto the desk's front edge; " +
                                       "model: almost any other small object, made from a picture (a phone or headphone stand, a box, a figure, a holder of any shape)";

        private const string Shared = @"You are one of three small toy teammates on the person's desk in mixed reality (Spark, Maker, Fixer). The crew helps one person make a real, 3D-printable object that fits their own things.
- Do the work. Be concrete: materials, colours, textures, sizes. Never interview.
- Plain, simple English only (the headset font is English only). 'say' is spoken aloud by your character: one short sentence, at most 12 words, friendly and in character, no lists, no emojis.
- Never repeat what is already in talk.";

        // F29: the crew decides what to do next, as actions the app performs in the room.
        public static readonly string[] Acts = { "say", "ask", "sticker", "vote", "pros_cons", "look_around", "do", "flag" };
        public static readonly string[] Parts = { "rim", "base", "side", "top", "inside", "none" };
        public static readonly string[] Moods = { "like", "worry", "idea", "none" };
        private const string Turn = @"You are the whole maker crew: three small toy teammates on the person's desk in mixed reality. They help one person invent and make a real, 3D-printable object for their own space.
- Spark: curious explorer. Inspiration, playful twists, unexpected variations, notices things in the room.
- Maker: calm craftsman and the lead. Form and look, sums up, proposes the next step.
- Fixer: careful and a little dry. Function and reality: how it is used, heat, size, where it sits, printing. Asks sharp questions.
You get the workbench (what is being made and where things stand), the latest event and the recent talk. workbench.personRules are the person's own standing rules for the shape: never suggest, flag, vote or sticker for anything against them (e.g. never 'keep the bumps' when a rule says no bumps). Choose 1 to 3 short actions that help the person's object move forward. Keep each teammate in role and vary what they do: a question, an opinion, a disagreement, a proposal, a twist. People find it most useful when the crew asks back about how the object will be used, so ask one sharp question when the use is unclear. Teammates may disagree when it is honest.
Acts:
- say: an opinion, answer or proposal.
- ask: one question for the person. options = 2 or 3 short answers the person can tap instead of speaking (1 to 4 words each, plain statements, never questions, each a complete answer that moves the object forward, e.g. 'Yes, hot coffee', 'Just cold drinks', 'Not sure').
- sticker: an opinion stuck on look number 'look' (0-based) with mood like | worry | idea. tag = that opinion in 1 or 2 words, at most 12 letters, printed on the sticker (e.g. 'cozy ribs', 'glaze chips', 'leaf rim'); 'say' is the full thought, which the person can open by tapping the sticker.
- vote: in a comparison, the teammate stands under look number 'look' and says why. Each teammate votes once; they may pick different looks.
- pros_cons: on look number 'look' (or -1 for the current design or made object): pros and cons, each 2 to 4 words, at most 3 each, and 'say' gives a one-line verdict.
- look_around: Spark goes to look around the room for inspiration (only Spark).
- do: the person gave an instruction, not a question (change the design, pick, make...): Maker carries it out; put the instruction, in the person's words, in 'say'.
- flag: after something is made, a see-through copy of it (v2) stands beside it. The teammate stands a little flag by itself with one change worth trying on v2; part = rim | base | side | top | inside is where on the object the change goes. 'say' is the change itself in at most 6 words (e.g. 'Add a thumb notch', 'Thicker base, less tipping'). The person plants the flags they want on v2 and confirms; only planted flags change it. Each teammate flags something different, in role, never one already on v2 (workbench.draft).
'look' indexes the looks; when there are no looks but idea notes are up (workbench.ideaNotes, before anything is chosen), it indexes the idea notes; else the versions of the made object (to compare or vote on them).
Events:
- said: the person just spoke. Answer what they asked; for 'compare' use vote (all three) and then ONE pros_cons on the look most of them voted for (4 actions); for pros and cons alone use pros_cons.
- looks_shown: looks just appeared. Exactly 3 actions, all sticker, one per teammate, each judging from its own angle for THIS person's use (the need and the talk): Spark what stands out or delights, Maker the form, Fixer how it works, holds, cleans or prints. tag = a verdict about that use in 1 or 2 words, never a plain adjective (e.g. 'won't tip', 'fits 12 pens', 'hard to clean', 'easy print'). Maker sticks on the look it recommends (mood like) and its 'say' is 'I'd pick this one: ' and the reason for this person in at most 10 words. Stickers are not spoken: each 'say' is read only when the person taps that sticker, one sentence of at most 12 words.
- idle: the person has been quiet. One teammate takes initiative with something new (a question, a twist or a risk), never a repeat.
- return: the person came back.
- made: the object was just made and its see-through v2 stands beside it. Each teammate offers one flag with a change worth trying for v2.
Rules: 'say' is spoken aloud: one short sentence, at most 12 words, plain English (the headset font is English only), no emojis, no lists. Say less: usually ONE action (one teammate speaks), two at most, except looks_shown and when the person asks to compare or for pros and cons. Use look = -1, mood = none and part = none when they do not apply, tag = '' unless the act is sticker, and empty pros and cons unless the act is pros_cons. Any action whose 'say' asks the person something gives 2 or 3 options (tap answers); all others use options = []. Never repeat anything already in talk.";

        private static string Instructions(string job)
        {
            switch (job)
            {
                case "name":
                    return Shared + @"
Name the FINISHED 3D object in the attached render. Combine BOTH the distinctive visible silhouette from the image AND the intended use in topic. The image is the source of truth for shape and structure; topic is the source of truth for purpose (a stand for headphones must remain a headphone stand, not just a star ornament). Ignore earlier concept labels and requested shape features absent from the final object. Return name: 2 to 4 plain English words, such as 'Heart tray', 'Star headphone stand', 'Round pen holder' or 'Leaf coaster'. Never use a design-direction label such as 'Open center' or 'Split rim', a version number, a marketing slogan, or an invented material. Do not describe the background or tags. If the silhouette is unclear, use the intended everyday object type without guessing a shape.";
                case "ideas":
                    return Shared + @"

The person can't think of what to make, so the crew holds a quick idea meeting in their room. You get the objects the camera sees right now ('seen'), the real spots around the person ('spots': the seen objects, 'desk edge', 'empty desk space'), what was made before ('made') and whether the 3D builder is ready ('meshy'). Each teammate sticks ONE idea on a real spot, so give exactly 3 ideas, one per teammate, each for a different spot when possible:
- Spark: curious and playful, something fun or delightful for that spot.
- Maker: calm craftsman, something genuinely useful every day.
- Fixer: careful, the fix for a small annoyance it can see (clutter, a cable that falls, a ring mark...).
What the crew can make (kind: what it is): " + Makeable + @". Never use kind 'model' when meshy is false. Never suggest something already in 'made'.
label = 2 to 4 plain everyday words anyone gets at a glance: the thing and what it is for (e.g. 'Coaster for your mug', 'Key tray', 'Headphone hook', 'Charger cable clip'); never an invented name or jargon (not 'Laptop cable stop'). The 3 ideas are 3 different kinds of things (never two clips or two holders). forObject = exactly one of the spots. kind = the kind. why = the everyday benefit in at most 8 words. need = one sentence saying what to make and what it is for, as the person would say it: the object, its use and where it goes; no colours, materials, textures, patterns, bumps or decoration (the person chooses those later), e.g. 'A coaster for my tumbler on the desk'. say = Spark's one excited line to open the notes (at most 12 words).";
                case "forms":
                    return Shared + @"

You are Maker, a calm, quick craftsman. Make 3 clearly different SHAPE sketches for the object (keep the object type). They are drawn as plain white clay models, so describe only the form: silhouette, proportions, edges, openings, feet, handles, how it holds what it is for. Never mention colours, materials, patterns or finishes (those come later). 'need' says what the object is and what it is for; when it came from an idea note, its look details (colours, textures, patterns, bumps) are only suggestions and never go over the person's own shape. 'rules' are the person's own standing rules for the shape (e.g. 'Flat, smooth bottom: no bumps'): all 3 sketches follow them, they win over everything else. Draw plain smooth surfaces unless the person asked for a texture or relief. 'shape' is what the person gave for its shape (their words; a photo of a real object or their own drawing goes to the picture model with your prompt, so describe the shape in words only as far as they said it): all 3 keep that shape (its outline) and differ clearly in the form (see looks 2 and 3 below). The person's pictures and drawings come with this request as images, in the order 'shape' lists them as reference images: look at them. When one of them is given as the whole shape (or is the only picture and nothing says otherwise), look 1 is exactly that shape as a clean model: the same parts in the same places, the same proportions and outline, nothing added, nothing redesigned; its label is 'Your drawing' (or 'Your picture'), and its prompt describes those parts precisely (e.g. 'a square box base, a thin straight post from its middle, a pointed triangular shade on top'). Looks 2 and 3 keep that outline recognisable but each takes ONE clearly different direction for the form, easy to tell apart at a glance from a metre away: a change of structure the picture clearly shows, e.g. a raised rim or a hollow, a body lifted on a foot ring or little feet, a tab or finger notch to lift it, two stacked layers, an opening or a cut-out. Never a small tweak (a slightly softer notch, a slightly taller lip); thickness or edge rounding alone is not a direction; looks 2 and 3 change different things (e.g. one the walls or the hollow, the other the base or a part added). Each direction must still suit what it is for and how it sits (a coaster stays a low piece a cup stands on; a pen holder stays an upright cup), lying or standing the same way up as look 1, with the same outline seen from above. Their prompts describe the whole form in that direction (the picture model gets the person's picture as 'copy exactly' only for look 1, and as 'keep this outline, follow the prompt' for looks 2 and 3); their labels name the direction (e.g. 'Deep dish', 'Stepped edge'). seen = what you see in the person's pictures, in at most 12 plain words, naming the parts (e.g. 'a square base, a thin post and a pointed shade'); '' when there are no pictures. 'shape' may list several things the person collected, each with what it is for (the whole shape, a named part such as the handle or the top view, or a detail): use each for exactly that, and combine them into one object. If it says more than one thing was given as the whole shape, the 3 sketches are different readings instead: one for each whole shape (with the other as a detail or part of it) and one that blends them, and each label names its reading. If 'shape' says the crew picks, give 3 different good forms. 'forObject' is the real thing it is for, if any. label = 2 or 3 words naming the form. prompt = an image prompt (max 40 words) describing that object's form precisely. say = which one you like and why, in a few words. sizes = the 1 to 3 real sizes of the WHOLE object that matter for THIS object, most important first: axis W (width, side to side), D (depth, front to back) or H (height); label = the question in 2 or 3 plain words ('how tall', 'how wide', 'how deep'); why = why that size matters for this object, at most 8 words ('tall enough to hide your pens'); typical = a usual size in mm; must = true for the 1 or 2 sizes the person should show, false for one the crew can choose. A coaster needs only its width, a pen holder its height and width, a tray its width and depth.";
                case "parts": // F83: what Meta's SAM is asked to find on the model, one word at a time
                    return Shared + @"

You name the parts of a thing that is about to be made as a small 3D object, so each part can be found on it, painted and reshaped on its own. 'thing' says what it is; the picture (when there is one) is the chosen sketch of its shape. parts = the 2 to 5 parts a person would point at and name, each clearly its own piece of the shape in the picture: name = ONE common lower-case English noun, singular (beak, wing, tail, leg, ear, handle, lid, spout, shade, base, foot, wheel, stem, roof, knob); ko = the same part as one everyday Korean word. Only parts that stick out or are plainly separate; a part that comes in pairs is named once. Never 'body', never a surface or place (top, side, bottom, front, inside, edge, rim), never a material, colour, pattern or hole. A plain one-piece thing (a coaster, a ball, a plain box, a simple tray) has no parts: return an empty list.";
                case "card": // F56: the person's own words for the shape, written on a small card in English
                    return Shared + @"

You are Maker. 'words' is what the person just said about the SHAPE of the thing being made ('making'), in any language. Write it on a small card: say = the same meaning in plain English, at most 8 words, only what they said about the shape (no greeting, no advice, nothing added).";
                case "finishes":
                    return Shared + @"

You are Maker, a calm, quick craftsman. The object is already made in its shape ('made' describes it). Make 3 clearly different FINISH versions of exactly that object for the person's 'vibe': only colours, materials, surface texture and painted decoration change, never the shape. 'rules' are the person's own rules for the shape: the object already follows them; never paint, add or suggest anything against them (e.g. no ripples or relief when a rule says no bumps). 'palette' lists colours and textures the person picked in their room; use them when there are any. label = 2 or 3 words naming the finish. prompt = an image prompt (max 40 words) describing the same object with that finish precisely. say = which one you like and why, in a few words.";
                case "looks":
                    return Shared + @"

You are Maker, a calm, quick craftsman. Make 3 clearly different picture references for the object (keep the object type). 'rules' are the person's own standing rules for the shape: all 3 follow them, they win over everything else. 'shape' is what the person gave for its shape (their words; a photo of a real object or their own drawing goes to the picture model with your prompt, so describe the shape in words only as far as they said it): all 3 keep that shape and differ in details. If 'shape' says the crew picks, give 3 different good shapes. 'style' is the colours and materials they gave (words, palette colours with hex codes, a reference photo): all 3 use those colours and differ in finish, edge and decoration; if the crew picks, vary material, colour and decoration. label = 2 or 3 words naming the look. prompt = an image prompt (max 45 words) describing that object and look precisely. say = which one you like and why, in a few words. sizes = the 1 to 3 real sizes of the WHOLE object that matter for THIS object, most important first: axis W (width, side to side), D (depth, front to back) or H (height); label = the question in 2 or 3 plain words ('how tall', 'how wide', 'how deep'); why = why that size matters for this object, at most 8 words ('tall enough to hide your pens'); typical = a usual size in mm; must = true for the 1 or 2 sizes the person should show, false for one the crew can choose. A coaster needs only its width, a pen holder its height and width, a tray its width and depth.";
                case "design":
                    return Shared + @"

You are Maker. Turn the need, the chosen look, the measured size and the asked shape (and any change just asked) into ONE coaster the app builds itself, in millimetres: cupDiameter = the measured size (never change it), shape (round | hex | octagon | square | flower), clearance (gap around the foot, 0.5-8), rimWidth (3-20), rimHeight (0-12), baseThickness (2-6), ribs (0-12 raised lines for drips), ribHeight (0.6-2), why (max 8 words), color = the main colour of the chosen look as #rrggbb, name (max 3 words). say = what you are about to show on their mug, in one sentence.";
                case "turn":
                    return Turn;
                case "check":
                    return Shared + @"

You are Fixer, careful and a little dry, but you always solve it. The app already checked the gap. Give the ONE most useful practical note about using or printing this exact object (heat and PLA, thin parts, drips, the handle...), and what to do. Never claim you changed anything. At most 16 words.";
                default: // material
                    return Shared + @"

You are Fixer. Search the web. material = the filament to print this object in and why, max 8 words (PLA softens around 55-60 C, so things under hot mugs usually need PETG or ASA). say = that advice as one spoken sentence.";
            }
        }

        public static JObject Schema(string job)
        {
            var str = new JObject { ["type"] = "string" };
            var num = new JObject { ["type"] = "number" };
            JObject props;
            JArray required;
            switch (job)
            {
                case "name":
                    props = new JObject { ["name"] = str };
                    required = new JArray("name");
                    break;
                case "ideas":
                    props = new JObject
                    {
                        ["say"] = str,
                        ["ideas"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                            ["properties"] = new JObject
                            {
                                ["who"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Names) }, ["label"] = str, ["forObject"] = str,
                                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("coaster", "vessel", "tray", "clip", "model") }, ["why"] = str, ["need"] = str
                            },
                            ["required"] = new JArray("who", "label", "forObject", "kind", "why", "need") } }
                    };
                    required = new JArray("say", "ideas");
                    break;
                case "looks":
                case "forms":
                case "finishes":
                    props = new JObject
                    {
                        ["say"] = str,
                        ["looks"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                            ["properties"] = new JObject { ["label"] = str, ["prompt"] = str }, ["required"] = new JArray("label", "prompt") } }
                    };
                    required = new JArray("say", "looks");
                    // F60: what Maker sees in the person's pictures, said back to them (forms only).
                    if (job == "forms") { props["seen"] = str; required.Add("seen"); }
                    // F61: the sizes this object needs (asked one by one at the measuring step).
                    if (job != "finishes")
                    {
                        props["sizes"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                            ["properties"] = new JObject { ["axis"] = new JObject { ["type"] = "string", ["enum"] = new JArray("W", "D", "H") }, ["label"] = str, ["why"] = str, ["typical"] = num, ["must"] = new JObject { ["type"] = "boolean" } },
                            ["required"] = new JArray("axis", "label", "why", "typical", "must") } };
                        required.Add("sizes");
                    }
                    break;
                case "design":
                    var design = new JObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false,
                        ["properties"] = new JObject
                        {
                            ["name"] = str, ["shape"] = new JObject { ["type"] = "string", ["enum"] = new JArray(CoasterDesign.Shapes) },
                            ["cupDiameter"] = num, ["clearance"] = num, ["rimWidth"] = num, ["rimHeight"] = num, ["baseThickness"] = num,
                            ["ribs"] = new JObject { ["type"] = "integer" }, ["ribHeight"] = num, ["why"] = str, ["color"] = str
                        },
                        ["required"] = new JArray("name", "shape", "cupDiameter", "clearance", "rimWidth", "rimHeight", "baseThickness", "ribs", "ribHeight", "why", "color")
                    };
                    props = new JObject { ["say"] = str, ["design"] = design };
                    required = new JArray("say", "design");
                    break;
                case "check":
                case "card": // F56
                    props = new JObject { ["say"] = str };
                    required = new JArray("say");
                    break;
                case "parts": // F83
                    props = new JObject
                    {
                        ["parts"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                            ["properties"] = new JObject { ["name"] = str, ["ko"] = str }, ["required"] = new JArray("name", "ko") } }
                    };
                    required = new JArray("parts");
                    break;
                case "turn":
                    var words = new JObject { ["type"] = "array", ["items"] = str };
                    var action = new JObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false,
                        ["properties"] = new JObject
                        {
                            ["who"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Names) },
                            ["act"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Acts) },
                            ["look"] = new JObject { ["type"] = "integer" },
                            ["mood"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Moods) },
                            ["part"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Parts) },
                            ["say"] = str, ["tag"] = str, ["pros"] = words, ["cons"] = words, ["options"] = words
                        },
                        ["required"] = new JArray("who", "act", "look", "mood", "part", "say", "tag", "pros", "cons", "options")
                    };
                    props = new JObject { ["actions"] = new JObject { ["type"] = "array", ["items"] = action } };
                    required = new JArray("actions");
                    break;
                default:
                    props = new JObject { ["say"] = str, ["material"] = str };
                    required = new JArray("say", "material");
                    break;
            }
            return new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = props, ["required"] = required };
        }

        // F60: with the person's pictures (JPEGs) as images after the input (Maker looks at them when it writes the
        // shape sketches; it used to read only "reference image 1 is for the whole shape").
        public static string CreateRequest(string job, object input, System.Collections.Generic.IList<byte[]> images)
        {
            if (images == null || images.Count == 0) return CreateRequest(job, input);
            var request = JObject.Parse(CreateRequest(job, input));
            var content = new JArray { new JObject { ["type"] = "input_text", ["text"] = JsonConvert.SerializeObject(input) } };
            foreach (var jpeg in images)
                if (jpeg != null && jpeg.Length > 0)
                    content.Add(new JObject { ["type"] = "input_image", ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg), ["detail"] = "auto" });
            request["input"] = new JArray { new JObject { ["role"] = "user", ["content"] = content } };
            return request.ToString(Formatting.None);
        }

        public static string CreateRequest(string job, object input) => new JObject
        {
            ["model"] = job == "check" || job == "ideas" || job == "turn" || job == "card" || job == "parts" ? "gpt-5.4-mini" : "gpt-5.4", ["store"] = false,
            ["instructions"] = Instructions(job), ["input"] = JsonConvert.SerializeObject(input),
            ["max_output_tokens"] = 4000,
            ["tools"] = job == "material" ? new JArray(new JObject { ["type"] = "web_search" }) : new JArray(),
            ["text"] = new JObject { ["format"] = new JObject { ["type"] = "json_schema", ["name"] = "windups_maker_" + job, ["strict"] = true, ["schema"] = Schema(job) } }
        }.ToString(Formatting.None);

        public static string ImageRequest(string prompt) => ImageRequest(prompt, ImageStyle);

        // F49: with another style (ClayStyle for the shape sketches).
        public static string ImageRequest(string prompt, string style) => new JObject
        {
            ["model"] = ImageModel, ["prompt"] = prompt.Trim() + style, ["size"] = "1024x1024",
            ["quality"] = ImageQuality, ["output_format"] = "jpeg", ["output_compression"] = 92, ["n"] = 1
        }.ToString(Formatting.None);

        // The reply text (the json object), robust to it arriving in two parts.
        public static string ReadOutput(string json)
        {
            var envelope = JObject.Parse(json);
            if ((string)envelope["status"] != "completed") throw new InvalidOperationException("Incomplete response");
            var texts = envelope["output"].Children().SelectMany(o => o["content"]?.Children() ?? Enumerable.Empty<JToken>())
                .Where(c => (string)c["type"] == "output_text").Select(c => (string)c["text"]).ToList();
            for (int i = texts.Count - 1; i >= 0; i--)
                try { JObject.Parse(texts[i]); return texts[i]; } catch (JsonException) { }
            return string.Concat(texts);
        }

        public static string Clean(string text) =>
            System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s*\(\[[^\]]*\]\([^)]*\)\)|\[([^\]]+)\]\([^)]*\)", m => m.Groups[1].Success ? m.Groups[1].Value : ""), @"https?://\S+", "").Trim();

        public static float Words(string text) => string.IsNullOrWhiteSpace(text) ? 0 : text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;

        // ------------------------------------------------------------ scripted (Editor preview and tests only)

        public static string ScriptedTurn(string kind, string said, int looks, bool hasDesign)
        {
            CrewAction A(string who, string act, int look, string mood, string say, string[] pros = null, string[] cons = null, string part = "none", string[] options = null, string tag = "") =>
                new CrewAction { who = who, act = act, look = look, mood = mood, part = part, say = say, tag = tag, pros = pros ?? new string[0], cons = cons ?? new string[0], options = options ?? new string[0] };
            string p = (said ?? "").ToLowerInvariant();
            CrewAction[] actions;
            if (kind == "looks_shown")
                actions = new[]
                {
                    A("Spark", "sticker", Mathf.Min(2, looks - 1), "like", "That wave really stands out on a desk!", tag: "stands out"),
                    A("Maker", "sticker", 0, "like", "I'd pick this one: the wide base won't tip.", tag: "won't tip"),
                    A("Fixer", "sticker", Mathf.Min(1, looks - 1), "worry", "Those thin walls could crack when printed.", tag: "thin walls")
                };
            else if (kind == "idle")
                actions = new[] { A("Spark", "say", -1, "idea", "What if it had a little notch for a tea bag?", options: new[] { "Yes, add a notch", "No notch" }) };
            else if (kind == "made")
                actions = new[]
                {
                    A("Spark", "flag", -1, "idea", "Add a little leaf on the rim", part: "rim"),
                    A("Fixer", "flag", -1, "worry", "Thicker base, less tipping", part: "base"),
                    A("Maker", "flag", -1, "idea", "Make it a bit taller", part: "top")
                };
            else if (kind == "return")
                actions = new[] { A("Maker", "ask", -1, "none", "Welcome back! What shall we make today?", options: new[] { "Another coaster", "Surprise me" }) };
            else if (p.StartsWith("(answering"))
                actions = new[] { A("Maker", "say", -1, "like", "Got it, thanks! That helps a lot.") };
            else if (p.Contains("compare") || p.Contains("비교") || p.Contains("which"))
                actions = new[]
                {
                    A("Spark", "vote", Mathf.Min(1, looks - 1), "like", "This one! It looks like it grew on the desk."),
                    A("Maker", "vote", 0, "like", "Walnut. It matches your desk best."),
                    A("Fixer", "vote", 0, "like", "Walnut too. The finish survives hot mugs."),
                    A("Fixer", "pros_cons", 0, "none", "Strong and easy to print, but plain.", new[] { "Easy to print", "Sturdy rim" }, new[] { "A bit plain", "Shows dust" })
                };
            else if (p.Contains("pros") || p.Contains("cons") || p.Contains("장단점") || p.Contains("장점") || p.Contains("단점"))
                actions = new[] { A("Fixer", "pros_cons", looks > 0 ? 0 : -1, "none", "Strong and easy to print, but plain.", new[] { "Easy to print", "Sturdy rim" }, new[] { "A bit plain", "Shows dust" }) };
            else if (p.Contains("blue") || p.Contains("taller") || p.Contains("bigger") || p.Contains("make it"))
                actions = new[] { A("Maker", "do", -1, "none", said) };
            else
                actions = new[] { A("Fixer", "ask", -1, "none", "Good question. Will it sit under hot coffee every day?", options: new[] { "Yes, hot coffee", "Cold drinks only", "Not sure" }) };
            return JsonConvert.SerializeObject(new TurnReply { actions = actions });
        }

        public static string Scripted(string job, float cup, string seen = "")
        {
            switch (job)
            {
                case "ideas":
                    return JsonConvert.SerializeObject(new IdeasReply
                    {
                        say = "Ooh, look what I found! Three ideas!",
                        ideas = new[]
                        {
                            new MakerIdea { who = "Spark", label = "Coaster for your mug", forObject = string.IsNullOrEmpty(seen) ? "" : seen, kind = "coaster", why = "No more wet rings", need = "A coaster that fits my mug." },
                            new MakerIdea { who = "Maker", label = "Key tray", forObject = "empty desk space", kind = "tray", why = "Keys always in one place", need = "A tray with pockets for my keys and phone." },
                            new MakerIdea { who = "Fixer", label = "Headphone hook", forObject = "desk edge", kind = "clip", why = "Headphones off the desk", need = "A hook for my headphones on the desk edge." }
                        }
                    });
                case "looks":
                    return JsonConvert.SerializeObject(new LooksReply
                    {
                        say = "Three looks. I like the walnut one.",
                        looks = new[]
                        {
                            new MakerLook { label = "Walnut leaves", prompt = "a round walnut-brown coaster with carved leaves" },
                            new MakerLook { label = "Sage clay", prompt = "a sage green matte clay-like coaster" },
                            new MakerLook { label = "Cream ribs", prompt = "a cream coaster with soft radial ribs" }
                        }
                    });
                case "forms": // F49
                    return JsonConvert.SerializeObject(new LooksReply
                    {
                        say = "Three shapes. I like the soft wave.",
                        seen = "",
                        looks = new[]
                        {
                            new MakerLook { label = "Soft wave", prompt = "a low stand with a gently curved saddle and rounded ends" },
                            new MakerLook { label = "Tall fin", prompt = "a low stand with a tall thin fin at the back" },
                            new MakerLook { label = "Pebble", prompt = "a smooth pebble-shaped stand with a shallow groove" }
                        }
                    });
                case "finishes": // F49
                    return JsonConvert.SerializeObject(new LooksReply
                    {
                        say = "Three finishes. The sunny one is my pick.",
                        looks = new[]
                        {
                            new MakerLook { label = "Sunny gloss", prompt = "the same stand in glossy sunflower yellow" },
                            new MakerLook { label = "Oak grain", prompt = "the same stand in light oak with fine grain" },
                            new MakerLook { label = "Coral speckle", prompt = "the same stand in matte coral with cream speckles" }
                        }
                    });
                case "design":
                    return JsonConvert.SerializeObject(new DesignReply
                    {
                        say = "A sage hexagon with a snug rim. Here it is on your mug.",
                        // A deliberately tight gap: Fixer catches it and fixes it in the fit check.
                        design = new CoasterDesign { name = "Sage Hex", shape = "hex", cupDiameter = cup, clearance = .6f, rimWidth = 6f, rimHeight = 4f, baseThickness = 3f, ribs = 6, ribHeight = 1f, why = "Holds the mug, sheds drips", color = "#8fb996" }
                    });
                case "check":
                    return JsonConvert.SerializeObject(new NoteReply { say = "Hot coffee softens PLA. Print this one in PETG." });
                case "card": // F56
                    return JsonConvert.SerializeObject(new NoteReply { say = "round body, wide base" });
                default:
                    return JsonConvert.SerializeObject(new MaterialReply { say = "Print it in PETG; PLA softens under hot coffee.", material = "PETG: stays firm under hot mugs" });
            }
        }
    }
}
