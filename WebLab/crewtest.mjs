// Command-line check of the working team: dispatch, run the jobs in parallel, print the cards.
// Run: node WebLab/crewtest.mjs "first message" ["second message"]
import { CREW, dispatcherInstructions, dispatcherSchema, workerInstructions, workerSchema, context, clean } from "./public/crew.js";
import { respond } from "./server.mjs";

const DISPATCH_MODEL = "gpt-5.4-mini", WORK_MODEL = process.env.WORK_MODEL || "gpt-5.4";
const state = { feed: [], cards: [], jobs: [] };
const name = id => CREW.find(c => c.id === id).name;

async function dispatch(event) {
  const out = await respond({ model: DISPATCH_MODEL, instructions: dispatcherInstructions(), input: JSON.stringify(context(state, { event, chainBudget: 0 })), schema: dispatcherSchema() });
  console.log(`\n[dispatch ${out.seconds.toFixed(1)}s]`);
  for (const l of out.result.lines) { console.log(`  ${name(l.agent)}: ${l.line}`); state.feed.push({ who: l.agent, text: l.line }); }
  return out.result.jobs;
}

async function work(job) {
  job.status = "running";
  state.jobs.push(job);
  console.log(`  ${name(job.agent)} takes: ${job.claim}  (assume: ${job.assumption})`);
  const out = await respond({ model: WORK_MODEL, instructions: workerInstructions(job.agent), input: JSON.stringify(context(state, { job: { task: job.task, assumption: job.assumption } })),
    schema: workerSchema(), web: job.agent === "scout" });
  job.status = "done";
  const r = out.result;
  const card = { id: "c" + (state.cards.length + 1), agent: job.agent, ...r, items: r.items.map(i => ({ name: clean(i.name), detail: clean(i.detail) })), takeaway: clean(r.takeaway), say: clean(r.say) };
  state.cards.push(card);
  state.feed.push({ who: job.agent, text: card.say });
  return { card, seconds: out.seconds, searched: out.searched };
}

for (const message of process.argv.slice(2)) {
  console.log(`\n=== Person: ${message}`);
  state.feed.push({ who: "me", text: message });
  const jobs = await dispatch({ type: "message", text: message });
  const done = await Promise.all(jobs.map(work));
  for (const { card, seconds, searched } of done) {
    console.log(`\n  [${name(card.agent)} card, ${seconds.toFixed(1)}s${searched ? ", searched" : ""}] ${card.title}${card.assumption ? "  (가정: " + card.assumption + ")" : ""}`);
    for (const it of card.items) console.log(`   - ${it.name}: ${it.detail}`);
    console.log(`   => ${card.takeaway}`);
    console.log(`   says: ${card.say}${card.question ? "  Q: " + card.question : ""}`);
  }
}
