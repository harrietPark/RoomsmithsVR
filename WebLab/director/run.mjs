// F48 bench runner: every case on every model, scored, with the time each call took.
// Run: node WebLab/director/run.mjs [model[:effort] ...] [--only id-part] [--show]
//   e.g. node WebLab/director/run.mjs gpt-5.4-mini gpt-5.4-mini:low gpt-5.4
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { loadContract, direct } from "./director.mjs";
import { cases } from "./cases.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const only = args.includes("--only") ? args[args.indexOf("--only") + 1] : "";
const show = args.includes("--show");
const models = args.filter((a, i) => !a.startsWith("--") && args[i - 1] !== "--only");
if (!models.length) models.push("gpt-5.4-mini");
const contract = loadContract();
const tool = name => contract.tools.find(t => t.name === name);

function judge(expect, r) {
  const why = [];
  const used = r.actions.map(a => a.tool);
  if (expect.intent && !expect.intent.includes(r.intent)) why.push("intent " + r.intent);
  if (expect.none && used.length) why.push("acted: " + used.join(","));
  const changed = used.filter(n => tool(n) && tool(n).changes);
  // (the app refuses a changing tool for a question / reaction / unclear, so that reading is safe anyway)
  const refused = ["question", "reaction", "unclear"].includes(r.intent);
  if (expect.noChange && changed.length && !refused) why.push("changed: " + changed.join(","));
  for (const f of expect.forbid || []) if (used.includes(f) && !(refused && tool(f).changes)) why.push("forbidden " + f);
  let hit = null;
  if (expect.any) {
    hit = r.actions.find(a => expect.any.includes(a.tool));
    if (!hit) why.push("wanted " + expect.any.join("|") + " got " + (used.join(",") || "nothing"));
    else if (refused && tool(hit.tool).changes) why.push("right tool but intent " + r.intent + " (the app would refuse it)");
  }
  if (hit && expect.arg) {
    const a = expect.arg;
    if (a.index !== undefined && hit.index !== a.index) why.push("index " + hit.index);
    if (a.who && hit.who !== a.who) why.push("who " + hit.who);
    if (a.mm !== undefined && Math.abs(hit.mm - a.mm) > 2) why.push("mm " + hit.mm);
    if (a.axis && hit.axis !== a.axis) why.push("axis " + hit.axis);
    if (a.part && hit.part !== a.part) why.push("part " + hit.part);
    if (a.textHas && !a.textHas.some(w => (hit.text || "").toLowerCase().includes(w))) why.push("text '" + hit.text + "'");
  }
  const words = r.lines.some(l => l.text.trim()) || !!r.ask.text.trim();
  const narrated = used.some(n => tool(n) && tool(n).narrates);
  if (expect.says === true && !words && !narrated) why.push("said nothing");
  if (expect.noAsk && r.ask.text.trim() && !narrated) why.push("asked back: " + r.ask.text);
  // Shown text must be English (the headset font has only Latin letters) and short.
  const shown = narrated ? [] : [...r.lines.map(l => l.text), r.ask.text, ...r.ask.options];
  const foreign = shown.find(x => /[\u0250-\u200f\u2060-\uffff]/.test(x));
  if (foreign) why.push("not English: " + foreign);
  if (!narrated && r.lines.filter(l => l.text.trim()).length > 2) why.push("more than two lines");
  const long = narrated ? null : r.lines.find(l => l.text.trim().split(/\s+/).length > 22);
  if (long) why.push("long line: " + long.text);
  // What would have really happened wrongly in the room: a change nobody asked for.
  const critical = (expect.none || expect.noChange) && changed.length > 0 && !refused
    || (expect.forbid || []).some(f => used.includes(f) && tool(f).changes && !refused);
  return { pass: why.length === 0, why, critical };
}

async function pool(items, size, work) {
  const out = new Array(items.length);
  let next = 0;
  await Promise.all(Array.from({ length: size }, async () => { while (next < items.length) { const i = next++; out[i] = await work(items[i], i); } }));
  return out;
}

const pct = (xs, p) => { const s = [...xs].sort((a, b) => a - b); return s.length ? s[Math.min(s.length - 1, Math.floor(p * s.length))] : 0; };
const picked = cases.filter(c => !only || c.id.includes(only));
const summary = [];
for (const spec of models) {
  const [model, effort = ""] = spec.split(":");
  const rows = await pool(picked, 6, async (c) => {
    try {
      const out = await direct({ contract, model, effort, state: c.state, recent: c.recent, heard: c.heard });
      return { id: c.id, said: c.heard.text, step: c.state.step, ...judge(c.expect, out.result), seconds: out.seconds, usage: out.usage, reply: out.result };
    } catch (error) { return { id: c.id, said: c.heard.text, step: c.state.step, pass: false, critical: false, why: ["ERROR " + error.message], seconds: 0, reply: null }; }
  });
  const ok = rows.filter(r => r.reply);
  const times = ok.map(r => r.seconds);
  const s = {
    model: spec, cases: rows.length, passed: rows.filter(r => r.pass).length, critical: rows.filter(r => r.critical).length, errors: rows.length - ok.length,
    seconds: { median: +pct(times, .5).toFixed(2), p90: +pct(times, .9).toFixed(2), max: +Math.max(0, ...times).toFixed(2) },
    tokens: ok.length ? { input: Math.round(ok.reduce((a, r) => a + (r.usage?.input_tokens || 0), 0) / ok.length), cached: Math.round(ok.reduce((a, r) => a + (r.usage?.input_tokens_details?.cached_tokens || 0), 0) / ok.length), output: Math.round(ok.reduce((a, r) => a + (r.usage?.output_tokens || 0), 0) / ok.length), reasoning: Math.round(ok.reduce((a, r) => a + (r.usage?.output_tokens_details?.reasoning_tokens || 0), 0) / ok.length) } : null
  };
  summary.push(s);
  console.log(`\n=== ${spec}: ${s.passed}/${s.cases} passed, ${s.critical} unasked changes, ${s.errors} errors | seconds median ${s.seconds.median} p90 ${s.seconds.p90} max ${s.seconds.max} | tokens in ${s.tokens?.input} (cached ${s.tokens?.cached}) out ${s.tokens?.output} (reasoning ${s.tokens?.reasoning})`);
  for (const r of rows) {
    if (r.pass && !show) continue;
    const a = r.reply ? r.reply.actions.map(x => x.tool + (x.text ? `("${x.text}")` : x.index >= 0 ? `(${x.index})` : x.who !== "none" ? `(${x.who})` : x.mm ? `(${x.axis} ${x.mm})` : "")).join(", ") : "";
    const l = r.reply ? r.reply.lines.map(x => `${x.who}: ${x.text || "(" + x.gesture + ")"}`).join(" / ") + (r.reply.ask.text ? ` ASK ${r.reply.ask.who}: ${r.reply.ask.text} [${r.reply.ask.options.join(" | ")}]` : "") : "";
    console.log(`${r.pass ? "ok  " : r.critical ? "BAD " : "miss"} ${r.id} [${r.step}] "${r.said}" -> ${r.reply ? r.reply.intent + "/" + r.reply.to : ""} ${a ? "{" + a + "}" : ""} ${l}${r.why.length ? "   <- " + r.why.join("; ") : ""}`);
  }
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  fs.mkdirSync(path.join(here, "results"), { recursive: true });
  fs.writeFileSync(path.join(here, "results", `${stamp}_${spec.replace(/[^\w.-]/g, "_")}.json`), JSON.stringify({ summary: s, rows }, null, 2));
}
console.log("\n" + JSON.stringify(summary, null, 2));
