// Command-line check: one real team turn per version (a few OpenAI calls).
// Run: node WebLab/test.mjs [presetIds, e.g. ACD] [model]
import { PRESETS, buildInstructions, buildSchema, buildInput, teamFor } from "./public/team.js";
import { respond } from "./server.mjs";

const TOPIC = "Windups를 진짜 쓸모있게 만들고 싶어. XR에서 캐릭터들이 내 아이디어 발전을 도와주는 앱인데, 지금은 말만 하고 나한테 도움이 안 돼.";
const only = process.argv[2] || PRESETS.map(p => p.id).join("");
const model = process.argv[3];

for (const preset of PRESETS.filter(p => only.includes(p.id))) {
  const config = { ...preset.config, ...(model ? { model } : {}) };
  const state = { config, log: [{ agent: null, say: TOPIC }], cards: [], board: [], summary: { idea: "", risk: "", next: "" }, teamTurns: 0 };
  const addressed = config.talk === "pick" ? teamFor(config).agents[teamFor(config).agents.length - 1].id : "";
  try {
    const out = await respond({ model: config.model, instructions: buildInstructions(config), input: buildInput(state, { type: "message", addressed }),
      schema: buildSchema(config), web: config.web === "on" });
    console.log(`\n=== ${preset.name} (${out.seconds.toFixed(1)}s${out.searched ? ", searched" : ""}, ${config.model})`);
    for (const t of out.result.turns) console.log(`  ${t.agent}: ${t.say}`);
    if (out.result.card.changed) console.log(`  [card] ${out.result.card.title} — ${out.result.card.pitch} (${out.result.card.change})`);
    if (out.result.board.changed) console.log(`  [board] ${out.result.board.notes.map(n => n.pile + ":" + n.text).join(" | ")}`);
    console.log(`  [summary] ${out.result.summary.idea} / ${out.result.summary.risk} / ${out.result.summary.next}`);
  } catch (error) {
    console.log(`\n=== ${preset.name}: FAILED ${error.message}`);
  }
}
