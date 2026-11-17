// F48 Crew Director, the text bench side. The contract (rules, character sheets, step manuals, tools) is the
// same file the headset app reads: Assets/Resources/CrewDirector.json. This module builds the request exactly
// as Assets/Scripts/Windups/CrewDirector.cs does (keep the two in step).
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { readKey } from "../server.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
export const contractPath = path.resolve(here, "..", "..", "Assets", "Resources", "CrewDirector.json");
export const loadContract = () => JSON.parse(fs.readFileSync(contractPath, "utf8"));

export const CREW = ["Spark", "Maker", "Fixer"];
export const INTENTS = ["question", "instruction", "answer", "idea", "reaction", "unclear"];
export const PARTS = ["rim", "base", "side", "top", "inside", "none"];
export const AXES = ["width", "height", "depth", "all", "none"];
export const GESTURES = ["none", "nod", "shake", "idea", "cheer", "present", "wave", "tilt"];

// The tools the person's words may use right now: its step, and every flag it needs ("!flag" = must be off).
export function available(contract, state) {
  const flags = state.flags || {};
  return contract.tools.filter(t =>
    (t.steps.includes("*") || t.steps.includes(state.step)) &&
    t.needs.every(n => n.startsWith("!") ? !flags[n.slice(1)] : !!flags[n]));
}

export function instructions(contract, state) {
  const tools = available(contract, state);
  const tag = t => (t.changes ? " [changes]" : "") + (t.costly ? " [costly]" : "") + (t.narrates ? " [narrates]" : "");
  return contract.rules.join("\n") +
    "\n\nTHE CREW\n" + CREW.map(c => "- " + c + ": " + contract.sheets[c]).join("\n") +
    "\n\nTHIS STEP: " + state.step + "\n" + (contract.manuals[state.step] || "") + "\n" + contract.manuals.always +
    "\n\nTOOLS YOU MAY USE NOW\n" + tools.map(t => "- " + t.name + "(" + t.args.join(", ") + ")" + tag(t) + ": " + t.help).join("\n");
}

export function schema(contract, state) {
  const str = { type: "string" };
  const tools = available(contract, state).map(t => t.name);
  const obj = (properties) => ({ type: "object", additionalProperties: false, properties, required: Object.keys(properties) });
  return obj({
    lang: str,
    intent: { type: "string", enum: INTENTS },
    to: { type: "string", enum: [...CREW, "team"] },
    actions: { type: "array", items: obj({
      tool: { type: "string", enum: tools }, text: str, index: { type: "integer" },
      who: { type: "string", enum: [...CREW, "none"] }, part: { type: "string", enum: PARTS },
      axis: { type: "string", enum: AXES }, mm: { type: "number" } }) },
    lines: { type: "array", items: obj({ who: { type: "string", enum: CREW }, text: str, gesture: { type: "string", enum: GESTURES } }) },
    ask: obj({ who: { type: "string", enum: CREW }, text: str, options: { type: "array", items: str } }),
    note: str
  });
}

export const input = (state, recent, heard) => JSON.stringify({ state, recent: recent || [], heard });

// One Director call. effort: reasoning effort to ask for ("" = the model's default).
export async function direct({ contract, model, state, recent, heard, effort = "" }) {
  const key = readKey();
  if (!key) throw new Error("No OpenAI key");
  const body = {
    model, store: false, instructions: instructions(contract, state), input: input(state, recent, heard), max_output_tokens: 2000,
    ...(effort ? { reasoning: { effort } } : {}),
    text: { format: { type: "json_schema", name: "windups_director", strict: true, schema: schema(contract, state) } }
  };
  const started = Date.now();
  const r = await fetch("https://api.openai.com/v1/responses", {
    method: "POST", headers: { "Content-Type": "application/json", Authorization: "Bearer " + key }, body: JSON.stringify(body)
  });
  const json = await r.json();
  const seconds = (Date.now() - started) / 1000;
  if (!r.ok) throw new Error((json.error && json.error.message) || ("OpenAI " + r.status));
  const texts = (json.output || []).flatMap(o => o.content || []).filter(c => c.type === "output_text").map(c => c.text);
  if (json.status !== "completed" || !texts.length) throw new Error("Incomplete reply (" + json.status + ")");
  let result = null;
  for (const t of [...texts].reverse()) { try { result = JSON.parse(t); break; } catch { /* next */ } }
  if (!result) throw new Error("Unreadable reply");
  return { result, seconds, usage: json.usage };
}
