const bucket = "roomsmiths-creations";
const mime = { glb: "model/gltf-binary", fbx: "application/octet-stream", obj: "application/zip", stl: "model/stl", "3mf": "model/3mf", jpg: "image/jpeg" };
const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
const id = /^[a-zA-Z0-9_-]{1,72}$/;
const uuid = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;
const codePattern = /^[A-HJ-NP-Z2-9]{8}$/;
const mib = 1024 * 1024;

function reply(status, body) {
  return Response.json(body, { status, headers: { "Cache-Control": "no-store" } });
}

function supabase(env, route, init = {}) {
  const headers = new Headers(init.headers);
  const key = env.SUPABASE_SECRET_KEY.trim();
  if (!key.startsWith("sb_secret_")) throw new Error("Supabase credential is not a project secret key");
  headers.set("apikey", key);
  return fetch(env.SUPABASE_URL.trim().replace(/\/$/, "") + route, { ...init, headers });
}

async function data(env, route, init) {
  const response = await supabase(env, "/rest/v1/" + route, init);
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    const code = typeof error.code === "string" && /^[A-Z0-9]{5,8}$/.test(error.code) ? error.code : "unknown";
    throw new Error("Supabase Data API rejected the request (" + response.status + ", " + code + ")");
  }
  return response.status === 204 ? null : response.json();
}

const rpc = (env, name, args = {}) => data(env, "rpc/" + name, {
  method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(args),
});

async function device(req) {
  const token = /^Bearer ([a-f0-9]{64})$/i.exec(req.headers.get("Authorization") || "")?.[1];
  if (!token) return null;
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(token.toLowerCase()));
  return Array.from(new Uint8Array(bytes), byte => byte.toString(16).padStart(2, "0")).join("");
}

async function reserve(req, env, owner) {
  const length = Number(req.headers.get("Content-Length"));
  if (!Number.isSafeInteger(length) || length < 1 || length > 4096) return reply(413, { error: "Invalid metadata size." });
  const input = await req.json();
  if (!input || !id.test(input.id || "") || typeof input.name !== "string" || !input.name.trim() ||
      !input.files || typeof input.files !== "object" || Array.isArray(input.files))
    return reply(400, { error: "Invalid creation." });
  const files = Object.entries(input.files);
  if (!files.some(([kind]) => kind === "stl") || files.some(([kind, size]) => !Object.hasOwn(mime, kind) ||
      !Number.isSafeInteger(size) || size < 1 || size > (kind === "jpg" ? mib : 20 * mib)))
    return reply(400, { error: "Invalid file size or format." });
  const total = files.reduce((sum, [, length]) => sum + length, 0);
  if (total > 32 * mib) return reply(413, { error: "Creation exceeds 32 MiB." });
  const revision = crypto.randomUUID();
  const createdOn = typeof input.createdOn === "string" && /^\d{4}-\d{2}-\d{2}$/.test(input.createdOn)
    && !isNaN(Date.parse(input.createdOn)) ? input.createdOn : null;
  for (let attempt = 0; attempt < 3; attempt++) {
    const code = Array.from(crypto.getRandomValues(new Uint8Array(8)), b => alphabet[b & 31]).join("");
    const result = await rpc(env, "roomsmiths_reserve", {
      p_owner: owner, p_code: code, p_revision: revision, p_work: input.id, p_name: input.name.trim().slice(0, 100),
      p_description: typeof input.description === "string" ? input.description.slice(0, 160) : "",
      p_date: createdOn, p_files: input.files, p_bytes: total,
    });
    if (result?.revision) return reply(201, result);
    if (result?.error !== "code-collision") break;
  }
  return reply(409, { error: "Collection storage is full. Try again later." });
}

async function upload(req, env, owner, revision, kind) {
  const [record] = await data(env, "roomsmiths_uploads?revision=eq." + revision +
    "&select=files,created_at,retired,committed_at,roomsmiths_collections!inner(owner,expires_at)" +
    "&roomsmiths_collections.owner=eq." + owner);
  if (!record || record.retired || record.committed_at || Date.parse(record.created_at) + 3600000 <= Date.now() ||
      Date.parse(record.roomsmiths_collections.expires_at) <= Date.now()) return reply(404, { error: "Upload expired." });
  if (!record.files[kind] || Number(req.headers.get("Content-Length")) !== record.files[kind] || !req.body)
    return reply(400, { error: "Unexpected file size." });
  // Preserve Content-Length without reading or re-encoding the bytes in the Worker.
  const stream = new FixedLengthStream(record.files[kind]);
  const piping = req.body.pipeTo(stream.writable);
  try {
    const stored = await supabase(env, "/storage/v1/object/" + bucket + "/" + revision + "/" + kind, {
      method: "POST", headers: { "Content-Type": mime[kind], "x-upsert": "false" }, body: stream.readable,
    });
    await piping;
    if (stored.ok) return new Response(null, { status: 204 });
    console.error("Supabase Storage rejected the upload", stored.status);
    return reply(502, { error: "File storage unavailable." });
  } catch (error) {
    await piping.catch(() => {});
    throw error;
  }
}

async function collection(env, code, workId, kind) {
  const [record] = await data(env, "roomsmiths_collections?select=expires_at&code=eq." + code);
  if (!record || Date.parse(record.expires_at) <= Date.now()) return reply(404, { error: "Code not found or expired." });
  const works = await data(env, "roomsmiths_uploads?code=eq." + code + "&retired=eq.false&committed_at=not.is.null" +
    "&select=revision,work_id,name,description,created_on,files&order=committed_at.desc" + (workId ? "&work_id=eq." + workId : ""));
  if (!workId) return reply(200, { works: works.map(w => ({
    id: w.work_id, name: w.name, description: w.description, createdOn: w.created_on, formats: Object.keys(w.files),
  })) });
  const work = works[0];
  if (!work?.files[kind]) return reply(404, { error: "File not found." });
  const upstream = await supabase(env, "/storage/v1/object/authenticated/" + bucket + "/" + work.revision + "/" + kind);
  if (!upstream.ok) return reply(502, { error: "File storage unavailable." });
  return new Response(upstream.body, { headers: {
    "Content-Type": mime[kind], "Cache-Control": "private, no-store", "X-Content-Type-Options": "nosniff",
    "Content-Disposition": (kind === "jpg" ? "inline" : "attachment") + '; filename="' + workId + "." + (kind === "obj" ? "zip" : kind) + '"',
  } });
}

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    if (url.pathname === "/" || url.pathname === "/collect/") {
      const page = new URL(req.url);
      page.pathname = "/collect/index.html";
      return env.ASSETS.fetch(new Request(page, req));
    }
    if (!url.pathname.startsWith("/api/collect/")) return env.ASSETS.fetch(req);
    if (!env.SUPABASE_URL || !env.SUPABASE_SECRET_KEY) return reply(503, { error: "Storage setup is pending." });
    try {
      const parts = url.pathname.slice("/api/collect/".length).split("/");
      const peer = req.headers.get("CF-Connecting-IP") || "local";
      if (req.method === "GET" && (parts.length === 1 || parts.length === 3) && codePattern.test(parts[0])) {
        if (!(await env.COLLECT_READS.limit({ key: peer })).success) return reply(429, { error: "Try again in a minute." });
        if (parts.length === 3 && (!id.test(parts[1]) || !Object.hasOwn(mime, parts[2]))) return reply(404, { error: "File not found." });
        return await collection(env, ...parts);
      }
      if (parts[0] !== "uploads") return reply(404, { error: "Code not found or expired." });
      const owner = await device(req);
      if (!owner) return reply(401, { error: "Missing device credential." });
      if (!(await env.COLLECT_WRITES.limit({ key: peer })).success) return reply(429, { error: "Try again in a minute." });
      if (req.method === "POST" && parts.length === 1) return await reserve(req, env, owner);
      if (parts.length !== 3 || !uuid.test(parts[1])) return reply(404, { error: "Upload not found." });
      if (req.method === "PUT" && Object.hasOwn(mime, parts[2])) return await upload(req, env, owner, parts[1], parts[2]);
      if (req.method === "POST" && parts[2] === "commit") {
        const record = await rpc(env, "roomsmiths_commit", { p_owner: owner, p_revision: parts[1] });
        return record ? reply(201, record) : reply(409, { error: "Upload incomplete or expired." });
      }
      return reply(404, { error: "Upload not found." });
    } catch (error) {
      console.error(error instanceof Error ? error.message : "Collection request failed");
      return reply(error instanceof SyntaxError ? 400 : 502, { error: "Could not read or save the creation." });
    }
  },
  async scheduled(_event, env) {
    if (!env.SUPABASE_URL || !env.SUPABASE_SECRET_KEY) return;
    const expired = await rpc(env, "roomsmiths_expired_uploads");
    if (!expired.length) return;
    const prefixes = expired.flatMap(item => Object.keys(item.files).map(kind => item.revision + "/" + kind));
    const removed = await supabase(env, "/storage/v1/object/" + bucket, {
      method: "DELETE", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ prefixes }),
    });
    if (!removed.ok) throw new Error("Storage cleanup failed: " + removed.status);
    await removed.body?.cancel();
    await data(env, "roomsmiths_uploads?revision=in.(" + expired.map(item => item.revision).join(",") + ")", { method: "DELETE" });
  },
};
