// F48 bench cases: a state, what the person said, and what must (not) happen.
//  expect.intent  acceptable intents
//  expect.any     at least one action uses one of these tools
//  expect.none    no action at all
//  expect.noChange no action with a [changes] tool (the object, flags, pictures and step stay as they are)
//  expect.forbid  these tools must not be used
//  expect.arg     { index | who | mm (+-2) | axis | part | textHas (any of, lower case) } on the matching action
//  expect.says    true: some words must be said (a line or a question); false: no words
//  expect.noAsk   no question back
// Every reply must also be in English and at most two lines (checked for all cases).
const flags = (on = {}) => ({
  draft: false, confirming: false, v2Changed: false, planted: false, offered: false, looks: false, ideas: false, drawing: false, notebookOpen: false,
  showCircle: false, made: false, awaitingFit: false, freeMeasure: false, hasGuess: false, palette: true, canPin: false, ...on
});
const base = (step, more = {}, on = {}) => ({
  step, need: "a coaster for my mug", forObject: "mug", activity: "", flags: flags(on), looks: [], chosenLook: "", ideas: [],
  measuredMm: 0, sizeGuessMm: 0, shape: "", colours: "", made: "", draft: null,
  palette: ["walnut #7a5230", "sage #8fb996", "cream #f1e6d0"], decisions: ["What we're making: a coaster for my mug"], ...more
});

const LOOKS = [
  { index: 0, label: "Walnut leaves", prompt: "a round walnut-brown wooden coaster with small carved leaves on the rim" },
  { index: 1, label: "Sage clay", prompt: "a sage green matte clay-like coaster with a soft rounded lip" },
  { index: 2, label: "Navy ribs", prompt: "a navy blue coaster with soft radial ribs and a brass edge" }
];
const OFFERED = [
  { by: "Spark", text: "Add a little leaf on the rim", part: "rim" },
  { by: "Fixer", text: "Thicker base, less tipping", part: "base" },
  { by: "Maker", text: "Make it a bit taller", part: "top" }
];
const made = { chosenLook: "Sage clay", measuredMm: 85, made: "Sage Hex coaster, 96 x 14 x 96 mm (W x H x D)", shape: "hexagon with a soft lip", colours: "sage green, matte" };

const D0 = () => base("Done", { ...made, draft: { planted: [], offered: OFFERED, setAside: [], sizeMm: { width: 96, height: 14, depth: 96 }, firstOneMm: { width: 96, height: 14, depth: 96 } } },
  { made: true, draft: true, offered: true, canPin: true });
const D1 = () => base("Done", { ...made, draft: { planted: [{ by: "Fixer", text: "Thicker base, less tipping", part: "base" }, { by: "person", text: "Add a thumb notch", part: "rim" }], offered: [OFFERED[0], OFFERED[2]], setAside: [], sizeMm: { width: 96, height: 14, depth: 96 }, firstOneMm: { width: 96, height: 14, depth: 96 } } },
  { made: true, draft: true, offered: true, planted: true, v2Changed: true, canPin: true });
const D2 = () => { const s = D1(); s.flags.confirming = true; return s; };
const DN = () => base("Done", { ...made }, { made: true, canPin: true });
const M = () => base("Making", { chosenLook: "Sage clay", measuredMm: 85, activity: "carving the shape out of the wood block", shape: "hexagon with a soft lip", colours: "sage green, matte" });
const L = () => base("Looks", { looks: LOOKS, shape: "round", colours: "the crew picks" }, { looks: true, canPin: true });
const ME = () => base("Measuring", { chosenLook: "Sage clay", sizeGuessMm: 82, looks: [LOOKS[1]] }, { hasGuess: true });
const S = () => base("ShapeInput", {}, { showCircle: true });
const C = () => base("ColourInput", { shape: "round with a little lip" }, { showCircle: true });
const SG = () => base("Suggesting", { need: "", forObject: "", decisions: [] });
const SGI = () => base("Suggesting", { need: "", forObject: "", decisions: [], ideas: [
  { index: 0, label: "Coaster for your mug", by: "Spark" }, { index: 1, label: "Key tray", by: "Maker" }, { index: 2, label: "Headphone hook", by: "Fixer" }] }, { ideas: true });
const F = () => base("ForObject", {}, { showCircle: true });

const FI = () => base("Finish", { chosenLook: "Soft hexagon", measuredMm: 85, made: "Soft hexagon coaster in white clay, 96 x 14 x 96 mm (W x H x D)", shape: "hexagon with a soft lip", lookRound: "", vibeOffered: true }, { made: false });
const LF = () => base("Looks", { looks: LOOKS, lookRound: "finishes", made: "Soft hexagon coaster in white clay", shape: "hexagon with a soft lip" }, { looks: true, canPin: true });

const hot = { who: "Fixer", text: "Will it sit under hot coffee every day?", options: ["Yes, hot coffee", "Cold drinks only", "Not sure"] };
const which = { who: "Maker", text: "Which change goes on v2?", options: ["Spark's leaf", "Fixer's base", "Just taller"] };
const RECENT_MADE = ["App: the coaster is made and stands on the desk", "App: a see-through v2 stands beside it",
  "Spark: Add a little leaf on the rim", "Fixer: Thicker base, less tipping", "Maker: Make it a bit taller",
  "Maker: These are just our thoughts. Plant a flag on v2 for anything you want changed."];

const c = (id, state, text, expect, more = {}) => ({ id, state: state(), recent: more.recent || [], heard: { text, openQuestion: more.open || null, pointedAt: more.pointedAt || "", lookingAt: more.lookingAt || "" }, expect });

export const cases = [
  // ---- made, v2 beside it, nothing planted
  c("v2-make-nothing-planted", D0, "Can you make version two?", { intent: ["instruction"], forbid: ["plant_flag", "plant_crew_flag", "resize_v2", "change_made"], says: true }, { recent: RECENT_MADE }),
  c("v2-make-ko", D0, "버전 2 만들자", { intent: ["instruction"], forbid: ["plant_flag", "plant_crew_flag", "resize_v2"], says: true }, { recent: RECENT_MADE }),
  c("v2-make-with-fixer-ko", D0, "v2 만들어줘, 픽서 깃발로", { intent: ["instruction"], any: ["plant_crew_flag"], arg: { who: "Fixer" }, forbid: ["plant_flag"] }, { recent: RECENT_MADE }),
  c("v2-thanks", D0, "Thanks!", { intent: ["reaction"], noAsk: true, none: true }, { recent: RECENT_MADE }),
  c("v2-thanks-ja", D0, "ありがとう", { intent: ["reaction"], noAsk: true, none: true }, { recent: RECENT_MADE }),
  c("v2-cute", D0, "Wow, that's so cute", { intent: ["reaction"], noAsk: true, none: true }),
  c("v2-thinking", D0, "Hmm, let me think", { intent: ["reaction"], noAsk: true, none: true }),
  c("v2-answer-hot", D0, "Yes, hot coffee", { intent: ["answer"], noAsk: true, noChange: true, forbid: ["plant_flag", "start_confirm"] }, { open: hot }),
  c("v2-answer-hot-es", D0, "sí, café caliente todos los días", { intent: ["answer"], noAsk: true, noChange: true, forbid: ["plant_flag", "start_confirm"] }, { open: hot }),
  c("v2-answer-which", D0, "Fixer's base", { intent: ["answer"], any: ["plant_crew_flag"], arg: { who: "Fixer" }, forbid: ["plant_flag"] }, { open: which }),
  c("v2-own-flag", D0, "Add a handle on the side", { intent: ["idea", "instruction"], any: ["plant_flag"], arg: { textHas: ["handle"] } }),
  c("v2-own-flag-request", D0, "Can you add a little notch for a tea bag?", { intent: ["idea", "instruction"], any: ["plant_flag"], arg: { textHas: ["notch"] } }),
  c("v2-taller-number", D0, "Make it 2 cm taller", { intent: ["instruction", "idea"], any: ["resize_v2"], arg: { mm: 34, axis: "height" } }),
  c("v2-bigger", D0, "A bit bigger", { intent: ["instruction", "idea"], any: ["resize_v2"] }),
  c("v2-taller-es", D0, "¿Puedes hacerlo un poco más alto?", { intent: ["instruction", "idea"], any: ["resize_v2"] }),
  c("v2-take-spark", D0, "I like Spark's idea", { intent: ["idea", "instruction", "answer"], any: ["plant_crew_flag"], arg: { who: "Spark" } }),
  c("v2-flag-question", D0, "What does Fixer's flag mean?", { intent: ["question"], noAsk: true, noChange: true, says: true }),
  c("v2-print-question", D0, "Is it going to print okay?", { intent: ["question", "instruction"], any: ["print_check"] }),
  c("v2-print", D0, "Print it", { intent: ["instruction"], any: ["print"] }),
  c("v2-notes", D0, "Show me the notes", { intent: ["instruction", "question"], any: ["open_notebook"] }),
  c("v2-compare", D0, "Which one is better, v1 or v2?", { intent: ["question"], noChange: true }),
  c("v2-paint-how", D0, "How do I paint it?", { intent: ["question"], noAsk: true, none: true, says: true }),
  c("v2-how-big", D0, "How big is v2?", { intent: ["question"], noAsk: true, noChange: true, says: true }),
  c("v2-start-over", D0, "Let's make something else", { intent: ["instruction"], any: ["start_over"] }),
  c("v2-what-change", D0, "What should I change?", { intent: ["question"], noChange: true }),
  c("v2-fetch-colour", D0, "Spark, get the colour of my keyboard", { intent: ["instruction"], any: ["fetch_colour"], arg: { textHas: ["keyboard"] } }),
  c("v2-noise", D0, "uh the the", { intent: ["unclear", "reaction"], noAsk: true, none: true }),
  c("v2-not-for-crew", D0, "No, I was talking to my mom", { intent: ["unclear", "reaction"], noAsk: true, none: true }),
  // ---- flags planted
  c("v2-done", D1, "I'm done", { intent: ["instruction", "answer"], any: ["start_confirm"] }),
  c("v2-make-planted", D1, "Okay, make version two", { intent: ["instruction"], any: ["start_confirm"], forbid: ["plant_flag"] }),
  c("v2-remove-own", D1, "Take off my flag", { intent: ["instruction"], any: ["remove_flag"], arg: { who: "none" } }),
  c("v2-remove-fixer-ko", D1, "픽서 깃발은 빼줘", { intent: ["instruction"], any: ["remove_flag"], arg: { who: "Fixer" } }),
  // ---- Maker read the plan back and waits for a yes
  c("v2-confirm-go", D2, "Yeah, go ahead and build it", { intent: ["answer", "instruction"], any: ["confirm_build"] }),
  c("v2-confirm-wait", D2, "Wait, not yet", { intent: ["answer", "instruction"], any: ["cancel_confirm"], forbid: ["confirm_build"] }),
  c("v2-confirm-question", D2, "Will the notch make it weaker?", { intent: ["question"], forbid: ["confirm_build", "plant_flag"], says: true }),
  // ---- made, no v2
  c("made-blue", DN, "Make it blue", { intent: ["instruction", "idea"], any: ["change_made"], arg: { textHas: ["blue"] } }),
  c("made-printer", DN, "Is this too big for my printer?", { intent: ["question"], any: ["print_check"] }),
  c("made-great", DN, "It looks great", { intent: ["reaction"], noAsk: true, none: true }),
  // ---- Maker is building
  c("making-how-long", M, "How long will it take?", { intent: ["question"], noAsk: true, none: true, says: true }),
  c("making-how-long-ko", M, "얼마나 걸려?", { intent: ["question"], noAsk: true, none: true, says: true }),
  c("making-change", M, "Can you make the rim thicker?", { intent: ["instruction", "idea"], any: ["queue_change"], arg: { textHas: ["rim", "thick"] } }),
  c("making-wow", M, "Wow, look at it go", { intent: ["reaction"], noAsk: true, none: true }),
  c("making-what", M, "What are you doing now?", { intent: ["question"], noAsk: true, none: true, says: true }),
  c("making-notes", M, "Show me the notes", { intent: ["instruction"], any: ["open_notebook"], forbid: ["queue_change"] }),
  // ---- three look pictures
  c("looks-blue-one", L, "I like the blue one", { intent: ["instruction", "idea", "answer"], any: ["pick_look"], arg: { index: 2 } }),
  c("looks-not-sure", L, "Hmm, not sure", { intent: ["reaction"], noAsk: true, forbid: ["redraw_looks", "pick_look"] }),
  c("looks-compare", L, "Compare them", { intent: ["instruction", "question"], any: ["crew_opinions"] }),
  c("looks-pros-cons", L, "What are the pros and cons of the green one?", { intent: ["question"], forbid: ["pick_look", "redraw_looks"] }),
  c("looks-middle", L, "The middle one", { intent: ["instruction", "answer", "idea"], any: ["pick_look"], arg: { index: 1 } }),
  c("looks-redraw", L, "Can you draw new ones, more colourful?", { intent: ["instruction"], any: ["redraw_looks"], arg: { textHas: ["colour", "color"] } }),
  c("looks-coffee", L, "Which one would survive hot coffee?", { intent: ["question"], noAsk: true, forbid: ["pick_look", "redraw_looks"] }),
  c("looks-wood-ko", L, "나무 느낌 나는 걸로 할게", { intent: ["instruction", "idea", "answer"], any: ["pick_look"], arg: { index: 0 } }),
  c("looks-pin", L, "Pin them on the wall", { intent: ["instruction"], any: ["pin_looks"] }),
  c("looks-why-green", L, "Why is the second one green?", { intent: ["question"], noAsk: true, forbid: ["pick_look", "redraw_looks"], says: true }),
  // ---- measuring
  c("measure-how", ME, "How do I measure it?", { intent: ["question"], noAsk: true, noChange: true, says: true }),
  c("measure-said", ME, "It's about eight and a half centimetres", { intent: ["instruction", "answer", "idea"], any: ["set_size"], arg: { mm: 85 } }),
  c("measure-guess", ME, "Use your guess", { intent: ["instruction", "answer"], any: ["use_guess"] }),
  c("measure-again", ME, "That's wrong, let me do it again", { intent: ["instruction"], any: ["measure_again"] }),
  c("measure-why", ME, "Why do you need the size?", { intent: ["question"], noAsk: true, noChange: true, says: true }),
  // ---- the shape
  c("shape-thinking", S, "Hmm let me think", { intent: ["reaction"], noAsk: true, forbid: ["add_shape", "crew_picks", "inputs_done"] }),
  c("shape-said", S, "Round with a little lip", { intent: ["idea", "answer", "instruction"], any: ["add_shape"], arg: { textHas: ["round"] } }),
  c("shape-you-pick", S, "You pick", { intent: ["instruction", "answer"], any: ["crew_picks"] }),
  c("shape-what-mean", S, "What do you mean by shape?", { intent: ["question", "answer"], noAsk: true, noChange: true, says: true }),
  c("shape-this", S, "Like this one", { intent: ["instruction", "answer", "idea"], any: ["take_picture", "match_thing"] }),
  c("shape-draw", S, "Can I draw it?", { intent: ["instruction", "question"], any: ["draw_myself"] }),
  c("shape-said-ko", S, "둥글고 낮게 해줘", { intent: ["idea", "answer", "instruction"], any: ["add_shape"], arg: { textHas: ["round"] } }),
  c("shape-thats-all", S, "That's all", { intent: ["instruction", "answer"], any: ["inputs_done"] }),
  // ---- the colours
  c("colour-said", C, "Navy blue and brass", { intent: ["idea", "answer", "instruction"], any: ["add_colour"], arg: { textHas: ["navy"] } }),
  c("colour-lamp", C, "Same colour as my lamp", { intent: ["idea", "answer", "instruction"], any: ["match_thing", "fetch_colour"], arg: { textHas: ["lamp"] } }),
  c("colour-question", C, "What colours go with walnut?", { intent: ["question"], noAsk: true, noChange: true, says: true }),
  c("colour-palette", C, "Use my palette", { intent: ["instruction", "answer"], any: ["use_palette"] }),
  // ---- nothing chosen yet
  c("start-need", SG, "I want to make a coaster for my mug", { intent: ["instruction", "idea", "answer"], any: ["set_need"], arg: { textHas: ["coaster"] } }),
  c("start-ideas", SG, "What can I make?", { intent: ["question", "instruction"], any: ["find_ideas"], forbid: ["set_need"] }),
  c("start-dont-know", SG, "I don't know", { intent: ["reaction", "unclear", "answer", "question"], forbid: ["set_need", "find_ideas"] }),
  c("start-who", SG, "Who are you guys?", { intent: ["question"], noAsk: true, forbid: ["set_need", "find_ideas"], says: true }),
  c("start-need-ko", SG, "휴대폰 거치대 만들고 싶어", { intent: ["instruction", "idea", "answer"], any: ["set_need"], arg: { textHas: ["phone"] } }),
  c("ideas-fixer", SGI, "Let's do Fixer's idea", { intent: ["instruction", "answer", "idea"], any: ["pick_idea"], arg: { index: 2 } }),
  c("ideas-tell-more", SGI, "Tell me more about the key tray", { intent: ["question", "instruction"], forbid: ["pick_idea", "set_need"], says: true }),
  // ---- the thing it is for
  c("object-none", F, "I don't have it here", { intent: ["answer", "instruction"], any: ["skip_object"] }),
  c("object-there", F, "Okay, it's in the circle", { intent: ["answer", "instruction"], any: ["take_picture"] }),
  c("object-why", F, "Why do you need my mug?", { intent: ["question"], noAsk: true, noChange: true, says: true }),
  // ---- the finish step (F49): the white clay shape gets its colours
  c("finish-vibe", FI, "Warm walnut with a brass edge", { intent: ["idea", "answer", "instruction"], any: ["finish_vibe"], arg: { textHas: ["walnut"] } }),
  c("finish-vibe-ko", FI, "파스텔 톤으로 귀엽게", { intent: ["idea", "answer", "instruction"], any: ["finish_vibe"], arg: { textHas: ["pastel"] } }),
  c("finish-done", FI, "I like it like this, keep mine", { intent: ["instruction", "answer"], any: ["finish_done"], forbid: ["finish_vibe"] }),
  c("finish-skip", FI, "Skip, I know how", { intent: ["instruction"], any: ["finish_skip"], forbid: ["finish_vibe"] }),
  c("finish-how-texture", FI, "How do I take a texture?", { intent: ["question"], noAsk: true, none: true, says: true }),
  c("finish-what-colours", FI, "What colours would look good?", { intent: ["question"], forbid: ["finish_vibe", "finish_done"], says: true }),
  c("finish-wow", FI, "Whoa, it's white", { intent: ["reaction"], none: true }),
  c("finish-get-colour", FI, "Spark, grab the colour of my lamp", { intent: ["instruction"], any: ["fetch_colour"], arg: { textHas: ["lamp"] } }),
  c("finishes-pick", LF, "The navy one", { intent: ["instruction", "answer", "idea"], any: ["pick_look"], arg: { index: 2 } }),
  // ---- notebook open, last make's fit
  c("notebook-close", () => { const s = D0(); s.flags.notebookOpen = true; return s; }, "Close it", { intent: ["instruction"], any: ["close_notebook"] }),
  c("fit-tight", () => { const s = SG(); s.flags.awaitingFit = true; return s; }, "It was a bit tight", { intent: ["answer"], any: ["fit_feedback"], arg: { textHas: ["tight"] } },
    { open: { who: "Spark", text: "How did the coaster fit your mug?", options: ["It fit well", "Too tight", "Too loose"] } })
];
