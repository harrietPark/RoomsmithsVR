import fs from "node:fs/promises";
import { addEditingFormats } from "./convert-creation.mjs";
import { createReadStream } from "node:fs";
import path from "node:path";
import { createHash, randomBytes, randomInt } from "node:crypto";

const formats = {
  glb: "model/gltf-binary", stl: "model/stl", "3mf": "model/3mf",
  fbx: "application/octet-stream", obj: "application/zip",
  jpg: "image/jpeg",
};
const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
const bytesLimit = 56 * 1024 * 1024;
const day = 24 * 60 * 60 * 1000;
const codePattern = /^[A-HJ-NP-Z2-9]{8}$/;
const idPattern = /^[a-zA-Z0-9_-]{1,72}$/;
const buckets = new Map();
let lastSweep = 0;
let writeQueue = Promise.resolve();

function json(res, status, value) {
  res.writeHead(status, { "Content-Type": "application/json", "Cache-Control": "no-store" });
  res.end(JSON.stringify(value));
}

function allow(req, write) {
  const key = (req.socket.remoteAddress || "local") + (write ? ":write" : ":read");
  const now = Date.now();
  if (buckets.size > 1000) for (const [id, record] of buckets) if (record.reset < now) buckets.delete(id);
  if (!buckets.has(key) && buckets.size >= 2000) return false;
  let bucket = buckets.get(key);
  if (!bucket || bucket.reset < now) {
    bucket = { count: 0, reset: now + 60000 };
    buckets.set(key, bucket);
  }
  return ++bucket.count <= (write ? 10 : 90);
}

async function getJson(file) {
  try { return JSON.parse(await fs.readFile(file, "utf8")); }
  catch (error) { if (error.code === "ENOENT") return null; throw error; }
}

async function writeJson(file, data) {
  const temporary = file + "." + randomBytes(8).toString("hex") + ".tmp";
  await fs.writeFile(temporary, JSON.stringify(data), { flag: "wx", mode: 0o600 });
  try { await fs.rename(temporary, file); }
  finally { await fs.rm(temporary, { force: true }); }
}

async function readBody(req) {
  const chunks = [];
  let length = 0;
  if (Number(req.headers["content-length"]) > bytesLimit) return null;
  for await (const chunk of req) {
    length += chunk.length;
    if (length > bytesLimit) return null;
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString("utf8"));
}

async function sweep(root, now) {
  if (now - lastSweep < 3600000) return;
  lastSweep = now;
  for (const entry of await fs.readdir(root, { withFileTypes: true })) {
    if (!entry.isDirectory() || !codePattern.test(entry.name)) continue;
    const folder = path.join(root, entry.name);
    const collection = await getJson(path.join(folder, "collection.json"));
    if (!collection || collection.expires < now) await fs.rm(folder, { recursive: true, force: true });
  }
}

async function save(root, owner, input) {
  const now = Date.now();
  await fs.mkdir(root, { recursive: true, mode: 0o700 });
  await sweep(root, now);
  const ownerFile = path.join(root, owner + ".json");
  let code = (await getJson(ownerFile))?.code;
  let metadata = code && codePattern.test(code) ? await getJson(path.join(root, code, "collection.json")) : null;
  if (!metadata || metadata.owner !== owner || metadata.expires < now) {
    do {
      code = Array.from({ length: 8 }, () => alphabet[randomInt(alphabet.length)]).join("");
      try { await fs.mkdir(path.join(root, code), { mode: 0o700 }); break; }
      catch (error) { if (error.code !== "EEXIST") throw error; }
    } while (true);
    metadata = { owner, expires: now + 7 * day, works: [] };
  }
  const index = metadata.works.findIndex(w => w.id === input.id);
  if (index === -1 && metadata.works.length >= 80) return null;
  const folder = path.join(root, code);
  const revision = randomBytes(9).toString("hex");
  const work = {
    id: input.id, name: input.name.trim().slice(0, 100),
    description: typeof input.description === "string" ? input.description.slice(0, 160) : "",
    createdOn: /^\d{4}-\d{2}-\d{2}$/.test(input.createdOn || "") && !isNaN(Date.parse(input.createdOn)) ? input.createdOn : null,
    files: {},
  };
  const written = [];
  try {
    for (const [format, data] of input.files) {
      const name = revision + "." + format;
      const file = path.join(folder, name);
      await fs.writeFile(file, data, { flag: "wx", mode: 0o600 });
      work.files[format] = name;
      written.push(file);
    }
    const old = index < 0 ? null : metadata.works[index];
    if (old) metadata.works[index] = work;
    else metadata.works.unshift(work);
    await writeJson(path.join(folder, "collection.json"), metadata);
    await writeJson(ownerFile, { code });
    if (old) await Promise.allSettled(Object.values(old.files).map(name => fs.rm(path.join(folder, name), { force: true })));
    return { code, expires: new Date(metadata.expires).toISOString() };
  } catch (error) {
    // An installed manifest must remain readable if writing the owner index fails.
    const installed = await getJson(path.join(folder, "collection.json")).catch(() => null);
    if (!installed?.works.some(w => Object.values(w.files).includes(Object.values(work.files)[0])))
      await Promise.allSettled(written.map(file => fs.rm(file, { force: true })));
    throw error;
  }
}

export async function collectionRequest(req, res, url, root) {
  if (!url.pathname.startsWith("/api/collect/")) return false;
  const write = req.method === "POST" && url.pathname === "/api/collect/works";
  try {
    if (!allow(req, write)) { json(res, 429, { error: "Try again in a minute." }); return true; }
    if (write) {
      const token = /^Bearer ([a-fA-F0-9]{64})$/.exec(req.headers.authorization || "")?.[1];
      if (!token) { json(res, 401, { error: "Missing device credential." }); return true; }
      const input = await readBody(req);
      if (!input) { json(res, 413, { error: "The creation is too large." }); return true; }
      if (!idPattern.test(input.id || "") || typeof input.name !== "string" || !input.name.trim() || !input.files || Array.isArray(input.files) || typeof input.files !== "object") {
        json(res, 400, { error: "Invalid creation." }); return true;
      }
      const files = Object.entries(input.files);
      if (!files.some(([format]) => format !== "jpg") || files.some(([format, data]) =>
        !Object.hasOwn(formats, format) || typeof data !== "string" || !data || !/^[A-Za-z0-9+/]+={0,2}$/.test(data))) {
        json(res, 400, { error: "Invalid creation files." }); return true;
      }
      input.files = files.map(([format, encoded]) => [format, Buffer.from(encoded, "base64")]);
      const owner = createHash("sha256").update(token.toLowerCase()).digest("hex");
      const task = writeQueue.then(async () => {
        await addEditingFormats(input.files);
        return save(root, owner, input);
      });
      writeQueue = task.catch(() => {});
      const receipt = await task;
      json(res, receipt ? 201 : 409, receipt || { error: "This collection is full." });
      return true;
    }
    const match = /^\/api\/collect\/([A-Z0-9]{8})(?:\/([a-zA-Z0-9_-]{1,72})\/([a-z0-9]+))?$/.exec(url.pathname);
    if (req.method !== "GET" || !match || !codePattern.test(match[1])) {
      json(res, 404, { error: "Code not found or expired." }); return true;
    }
    const [, code, id, format] = match;
    const folder = path.join(root, code);
    const collection = await getJson(path.join(folder, "collection.json"));
    if (!collection || collection.expires < Date.now()) {
      json(res, 404, { error: "Code not found or expired." }); return true;
    }
    if (!id) {
      json(res, 200, { works: collection.works.map(w => ({
        id: w.id, name: w.name, description: w.description, createdOn: w.createdOn, formats: Object.keys(w.files),
      })) });
      return true;
    }
    const file = Object.hasOwn(formats, format) && collection.works.find(w => w.id === id)?.files[format];
    if (!file) { json(res, 404, { error: "File not found." }); return true; }
    const fullPath = path.join(folder, file);
    const stat = await fs.stat(fullPath);
    const extension = format === "obj" ? "-obj.zip" : "." + format;
    res.writeHead(200, {
      "Content-Type": formats[format], "Content-Length": stat.size, "Cache-Control": "private, no-store",
      "X-Content-Type-Options": "nosniff", "Content-Disposition": `${format === "jpg" ? "inline" : "attachment"}; filename="${id}${extension}"`,
    });
    const stream = createReadStream(fullPath);
    stream.on("error", () => res.destroy());
    res.on("close", () => stream.destroy());
    stream.pipe(res);
  } catch (error) {
    if (!res.headersSent && !res.destroyed) json(res, error instanceof SyntaxError ? 400 : 500, { error: "Could not read or save the creation." });
    else res.destroy();
  }
  return true;
}
