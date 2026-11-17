// Windups Lab local server: serves the page and relays AI calls to OpenAI with the project's
// local key (LocalSettings/openai-api-key.txt or Server/.env). The key never reaches the page.
// Run: node WebLab/server.mjs   then open http://localhost:5177
import http from "node:http";
import { collectionRequest } from "./collection.mjs";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..");
const pub = path.join(here, "public");
const sessions = path.join(here, "sessions");
const PORT = Number(process.env.PORT || 5177);

export function readKey() {
  const local = path.join(root, "LocalSettings", "openai-api-key.txt");
  if (fs.existsSync(local)) return fs.readFileSync(local, "utf8").trim();
  const env = path.join(root, "Server", ".env");
  if (fs.existsSync(env))
    for (const line of fs.readFileSync(env, "utf8").split(/\r?\n/)) {
      const m = line.match(/^\s*OPENAI_API_KEY\s*=\s*(.+)\s*$/);
      if (m) return m[1].replace(/^["']|["']$/g, "").trim();
    }
  return process.env.OPENAI_API_KEY || "";
}

const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".svg": "image/svg+xml" };

function body(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on("data", c => chunks.push(c));
    req.on("end", () => resolve(Buffer.concat(chunks)));
    req.on("error", reject);
  });
}

function send(res, status, data, type = "application/json; charset=utf-8") {
  res.writeHead(status, { "Content-Type": type, "Cache-Control": "no-store" });
  res.end(typeof data === "string" || Buffer.isBuffer(data) ? data : JSON.stringify(data));
}

// One team turn: Responses API with a strict JSON schema (and optionally web search).
export async function respond({ model, instructions, input, schema, web }) {
  const key = readKey();
  if (!key) throw new Error("No OpenAI key in LocalSettings/openai-api-key.txt");
  const payload = (tool) => ({
    model, store: false, instructions, input, max_output_tokens: 6000,
    ...(tool ? { tools: [{ type: tool }] } : {}),
    text: { format: { type: "json_schema", name: "windups_team", strict: true, schema } }
  });
  const started = Date.now();
  let tool = web ? "web_search" : null;
  let r = await fetch("https://api.openai.com/v1/responses", {
    method: "POST", headers: { "Content-Type": "application/json", Authorization: "Bearer " + key }, body: JSON.stringify(payload(tool))
  });
  if (!r.ok && tool && r.status === 400) {
    tool = "web_search_preview";
    r = await fetch("https://api.openai.com/v1/responses", {
      method: "POST", headers: { "Content-Type": "application/json", Authorization: "Bearer " + key }, body: JSON.stringify(payload(tool))
    });
  }
  const json = await r.json();
  if (!r.ok) throw new Error((json.error && json.error.message) || ("OpenAI " + r.status));
  const texts = (json.output || []).flatMap(o => o.content || []).filter(c => c.type === "output_text").map(c => c.text);
  if (json.status !== "completed" || !texts.length) throw new Error("Incomplete reply (" + json.status + ")");
  return { result: parseReply(texts), seconds: (Date.now() - started) / 1000, usage: json.usage, searched: (json.output || []).some(o => /web_search/.test(o.type || "")) };
}

// The reply is one JSON object; occasionally it arrives in two parts or twice in a row.
function parseReply(texts) {
  for (const text of [...texts].reverse().concat([texts.join("")])) {
    try { return JSON.parse(text); } catch { /* try the first complete object below */ }
    const start = text.indexOf("{");
    let depth = 0, inString = false, escaped = false;
    for (let i = start; start >= 0 && i < text.length; i++) {
      const c = text[i];
      if (inString) { if (escaped) escaped = false; else if (c === "\\") escaped = true; else if (c === '"') inString = false; continue; }
      if (c === '"') inString = true;
      else if (c === "{") depth++;
      else if (c === "}" && --depth === 0) { try { return JSON.parse(text.slice(start, i + 1)); } catch { break; } }
    }
  }
  throw new Error("Unreadable reply");
}

async function tts({ voice, text, style }) {
  const r = await fetch("https://api.openai.com/v1/audio/speech", {
    method: "POST", headers: { "Content-Type": "application/json", Authorization: "Bearer " + readKey() },
    body: JSON.stringify({ model: "gpt-4o-mini-tts", voice: voice || "alloy", input: text, instructions: style || "Speak naturally and warmly, like a teammate at a table.", response_format: "mp3" })
  });
  if (!r.ok) throw new Error("TTS " + r.status + " " + (await r.text()).slice(0, 200));
  return Buffer.from(await r.arrayBuffer());
}

async function stt(audio, type, language) {
  const form = new FormData();
  type = (type || "audio/webm").split(";")[0];
  const ext = { "audio/mpeg": "mp3", "audio/mp3": "mp3", "audio/ogg": "ogg", "audio/wav": "wav", "audio/mp4": "mp4", "audio/webm": "webm" }[type] || "webm";
  form.append("file", new Blob([audio], { type }), "speech." + ext);
  form.append("model", "gpt-4o-mini-transcribe");
  if (language) form.append("language", language);
  const r = await fetch("https://api.openai.com/v1/audio/transcriptions", {
    method: "POST", headers: { Authorization: "Bearer " + readKey() }, body: form
  });
  const json = await r.json();
  if (!r.ok) throw new Error((json.error && json.error.message) || ("STT " + r.status));
  return json.text || "";
}

export const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, "http://localhost");
    if (await collectionRequest(req, res, url, process.env.ROOMSMITHS_COLLECTION_DIR || path.join(here, "collections"))) return;
    if (req.method === "POST" && url.pathname === "/api/respond") return send(res, 200, await respond(JSON.parse(await body(req))));
    if (req.method === "POST" && url.pathname === "/api/tts") return send(res, 200, await tts(JSON.parse(await body(req))), "audio/mpeg");
    if (req.method === "POST" && url.pathname === "/api/stt")
      return send(res, 200, { text: await stt(await body(req), req.headers["content-type"], url.searchParams.get("lang")) });
    if (req.method === "POST" && url.pathname === "/api/session") {
      const data = JSON.parse(await body(req));
      const id = String(data.id || Date.now()).replace(/[^\w.-]/g, "_");
      fs.mkdirSync(sessions, { recursive: true });
      fs.writeFileSync(path.join(sessions, id + ".json"), JSON.stringify(data, null, 2));
      return send(res, 200, { saved: id });
    }
    // F26: the headset sends a print file (STL, millimetres) here; it lands in WebLab/prints.
    if (req.method === "POST" && url.pathname === "/api/print") {
      const data = await body(req);
      const name = String(url.searchParams.get("name") || ("print_" + Date.now() + ".stl")).replace(/[^\w.-]/g, "_");
      const prints = path.join(here, "prints");
      fs.mkdirSync(prints, { recursive: true });
      fs.writeFileSync(path.join(prints, name), data);
      console.log("Print file received: " + name + " (" + data.length + " bytes)");
      return send(res, 200, { saved: name, bytes: data.length });
    }
    if (req.method === "GET" && url.pathname === "/api/health") return send(res, 200, { ok: true, key: !!readKey() });
    // Static files.
    let file = path.normalize(path.join(pub, url.pathname === "/" ? "index.html" : url.pathname));
    if (!file.startsWith(pub) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) return send(res, 404, { error: "not found" });
    return send(res, 200, fs.readFileSync(file), TYPES[path.extname(file)] || "application/octet-stream");
  } catch (error) {
    console.error(error);
    return send(res, 500, { error: String(error.message || error) });
  }
});

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  server.listen(PORT, () => console.log("Windups Lab on http://localhost:" + PORT));
