import http from "node:http";
import fs from "node:fs/promises";
import { createReadStream } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { collectionRequest } from "./collection.mjs";

const publicDir = fileURLToPath(new URL("./public/collect/", import.meta.url));
const store = process.env.ROOMSMITHS_COLLECTION_DIR;
if (!store) throw new Error("Set ROOMSMITHS_COLLECTION_DIR to a persistent volume outside the public directory.");
const storePath = path.resolve(store);
const publicPath = path.resolve(publicDir);
const relativeStore = path.relative(publicPath, storePath);
if (!relativeStore || (!relativeStore.startsWith(".." + path.sep) && relativeStore !== ".." && !path.isAbsolute(relativeStore)))
  throw new Error("The collection store must not be publicly served.");
const types = {
  ".html": "text/html; charset=utf-8", ".css": "text/css; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".ttf": "font/ttf", ".png": "image/png", ".glb": "model/gltf-binary", ".stl": "model/stl",
  ".3mf": "model/3mf", ".zip": "application/zip", ".txt": "text/plain; charset=utf-8",
};

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, "http://localhost");
    if (await collectionRequest(req, res, url, storePath)) return;
    if (req.method !== "GET") { res.writeHead(405); res.end(); return; }
    if (url.pathname === "/" || url.pathname === "/collect/") {
      res.writeHead(302, { Location: "/collect/index.html" });
      res.end();
      return;
    }
    const parts = /^\/collect\/(?:(sample)\/)?([a-zA-Z0-9_.-]+)$/.exec(url.pathname);
    if (!parts || [".", ".."].includes(parts[2])) { res.writeHead(404); res.end(); return; }
    const file = path.join(publicPath, parts[1] || "", parts[2]);
    const info = await fs.stat(file);
    if (!info.isFile()) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, {
      "Content-Type": types[path.extname(file)] || "application/octet-stream",
      "Content-Length": info.size,
      "X-Content-Type-Options": "nosniff",
      "Referrer-Policy": "no-referrer",
      "Cache-Control": file.endsWith(".html") ? "no-cache" : "public, max-age=300",
    });
    const stream = createReadStream(file);
    stream.on("error", () => res.destroy());
    res.on("close", () => stream.destroy());
    stream.pipe(res);
  } catch (error) {
    if (!res.headersSent) { res.writeHead(error.code === "ENOENT" ? 404 : 500); res.end(); }
    else res.destroy();
  }
});
server.listen(Number(process.env.PORT || 5177), process.env.HOST || "127.0.0.1");
