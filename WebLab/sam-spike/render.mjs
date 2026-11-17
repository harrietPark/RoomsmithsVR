// Spike: draws a Meshy OBJ from several directions (clay and, with a texture, in colour) and keeps, for
// every pixel, the triangle it shows. No dependencies. Usage: node render.mjs <model.obj> <outDir> [texture.png]
import fs from 'node:fs';
import zlib from 'node:zlib';
import path from 'node:path';

export function parseObj(text) {
  const v = [], vt = [], tri = [], triUv = [];
  for (const raw of text.split('\n')) {
    const line = raw.trim();
    if (line.startsWith('v ')) { const p = line.split(/\s+/); v.push([+p[1], +p[2], +p[3]]); }
    else if (line.startsWith('vt ')) { const p = line.split(/\s+/); vt.push([+p[1], +p[2] || 0]); }
    else if (line.startsWith('f ')) {
      const p = line.split(/\s+/).slice(1).map(s => { const q = s.split('/'); return [(+q[0]) - 1, q[1] ? (+q[1]) - 1 : -1]; });
      for (let i = 1; i + 1 < p.length; i++) { tri.push([p[0][0], p[i][0], p[i + 1][0]]); triUv.push([p[0][1], p[i][1], p[i + 1][1]]); }
    }
  }
  // Centre it and make its largest side 1.
  const lo = [1e9, 1e9, 1e9], hi = [-1e9, -1e9, -1e9];
  for (const p of v) for (let k = 0; k < 3; k++) { lo[k] = Math.min(lo[k], p[k]); hi[k] = Math.max(hi[k], p[k]); }
  const size = Math.max(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]);
  for (const p of v) for (let k = 0; k < 3; k++) p[k] = (p[k] - (lo[k] + hi[k]) / 2) / size;
  // Smooth normals (area weighted).
  const n = v.map(() => [0, 0, 0]);
  for (const t of tri) {
    const a = v[t[0]], b = v[t[1]], c = v[t[2]];
    const f = cross(sub(b, a), sub(c, a));
    for (const i of t) { n[i][0] += f[0]; n[i][1] += f[1]; n[i][2] += f[2]; }
  }
  for (const q of n) { const m = Math.hypot(q[0], q[1], q[2]) || 1; q[0] /= m; q[1] /= m; q[2] /= m; }
  return { v, vt, tri, triUv, n };
}

const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const unit = a => { const m = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / m, a[1] / m, a[2] / m]; };

// The view from `azimuth` degrees round the upright axis, `elevation` degrees up. Orthographic.
export function camera(azimuth, elevation) {
  const a = azimuth * Math.PI / 180, e = elevation * Math.PI / 180;
  const back = [Math.sin(a) * Math.cos(e), Math.sin(e), Math.cos(a) * Math.cos(e)];   // from the model to the eye
  const right = unit(cross([0, 1, 0], back));
  const up = cross(back, right);
  return { azimuth, elevation, right, up, back };
}

// size: the picture's side in pixels; the model (largest side 1, so at most 1.74 across) fills `fill` of it.
export function project(cam, p, size, fill = .62) {
  const s = size * fill;
  return [size / 2 + dot(p, cam.right) * s, size / 2 - dot(p, cam.up) * s, dot(p, cam.back)];
}

// Returns { rgb: Uint8Array(size*size*3), id: Int32Array(size*size) } ; id is the triangle index or -1.
export function render(model, cam, size, texture, background = [236, 236, 236]) {
  const S = size * 2;                                   // drawn at twice the size, then halved (soft edges)
  const depth = new Float32Array(S * S).fill(-1e9);
  const ids = new Int32Array(S * S).fill(-1);
  const col = new Uint8Array(S * S * 3);
  for (let i = 0; i < S * S; i++) { col[i * 3] = background[0]; col[i * 3 + 1] = background[1]; col[i * 3 + 2] = background[2]; }
  const light = unit([cam.back[0] + cam.right[0] * .45 + cam.up[0] * .55, cam.back[1] + cam.right[1] * .45 + cam.up[1] * .55, cam.back[2] + cam.right[2] * .45 + cam.up[2] * .55]);
  const pts = model.v.map(p => project(cam, p, S));
  for (let t = 0; t < model.tri.length; t++) {
    const [i0, i1, i2] = model.tri[t];
    const a = pts[i0], b = pts[i1], c = pts[i2];
    const area = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
    if (Math.abs(area) < 1e-9) continue;
    const x0 = Math.max(0, Math.floor(Math.min(a[0], b[0], c[0]))), x1 = Math.min(S - 1, Math.ceil(Math.max(a[0], b[0], c[0])));
    const y0 = Math.max(0, Math.floor(Math.min(a[1], b[1], c[1]))), y1 = Math.min(S - 1, Math.ceil(Math.max(a[1], b[1], c[1])));
    const na = model.n[i0], nb = model.n[i1], nc = model.n[i2];
    const uv = model.triUv[t];
    const flat = texture && texture.parts ? model.triColour[t] : null;
    const ua = texture && !texture.parts && uv[0] >= 0 ? model.vt[uv[0]] : null, ub = ua ? model.vt[uv[1]] : null, uc = ua ? model.vt[uv[2]] : null;
    for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
      const px = x + .5, py = y + .5;
      const w0 = ((b[0] - px) * (c[1] - py) - (b[1] - py) * (c[0] - px)) / area;
      const w1 = ((c[0] - px) * (a[1] - py) - (c[1] - py) * (a[0] - px)) / area;
      const w2 = 1 - w0 - w1;
      if (w0 < 0 || w1 < 0 || w2 < 0) continue;
      const z = a[2] * w0 + b[2] * w1 + c[2] * w2;
      const k = y * S + x;
      if (z <= depth[k]) continue;
      depth[k] = z; ids[k] = t;
      let nx = na[0] * w0 + nb[0] * w1 + nc[0] * w2, ny = na[1] * w0 + nb[1] * w1 + nc[1] * w2, nz = na[2] * w0 + nb[2] * w1 + nc[2] * w2;
      const m = Math.hypot(nx, ny, nz) || 1; nx /= m; ny /= m; nz /= m;
      let d = nx * light[0] + ny * light[1] + nz * light[2];
      if (nx * cam.back[0] + ny * cam.back[1] + nz * cam.back[2] < 0) d = -d;     // a face seen from behind
      const shade = .42 + .58 * Math.max(0, d);
      let r = 226, g = 219, bl = 208;                                              // clay
      if (flat) { r = flat[0]; g = flat[1]; bl = flat[2]; }
      else if (ua) {
        const u = ua[0] * w0 + ub[0] * w1 + uc[0] * w2, vv = ua[1] * w0 + ub[1] * w1 + uc[1] * w2;
        const tx = Math.min(texture.w - 1, Math.max(0, Math.floor((u - Math.floor(u)) * texture.w)));
        const ty = Math.min(texture.h - 1, Math.max(0, Math.floor((1 - (vv - Math.floor(vv))) * texture.h)));
        const q = (ty * texture.w + tx) * 4;
        r = texture.data[q]; g = texture.data[q + 1]; bl = texture.data[q + 2];
      }
      col[k * 3] = Math.min(255, r * shade); col[k * 3 + 1] = Math.min(255, g * shade); col[k * 3 + 2] = Math.min(255, bl * shade);
    }
  }
  const rgb = new Uint8Array(size * size * 3), id = new Int32Array(size * size);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const k = y * size + x;
    for (let ch = 0; ch < 3; ch++) {
      const q = (y * 2) * S + x * 2;
      rgb[k * 3 + ch] = (col[q * 3 + ch] + col[(q + 1) * 3 + ch] + col[(q + S) * 3 + ch] + col[(q + S + 1) * 3 + ch]) >> 2;
    }
    id[k] = ids[(y * 2) * S + x * 2];
  }
  return { rgb, id };
}

// ---- PNG (8 bit, no interlace)
const crcTable = (() => { const t = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
function crc(buf) { let c = 0xffffffff; for (const b of buf) c = crcTable[(c ^ b) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; }
function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type), data]);
  const sum = Buffer.alloc(4); sum.writeUInt32BE(crc(body));
  return Buffer.concat([len, body, sum]);
}
export function encodePng(rgb, w, h) {
  const raw = Buffer.alloc((w * 3 + 1) * h);
  for (let y = 0; y < h; y++) Buffer.from(rgb.buffer, rgb.byteOffset + y * w * 3, w * 3).copy(raw, y * (w * 3 + 1) + 1);
  const head = Buffer.alloc(13); head.writeUInt32BE(w, 0); head.writeUInt32BE(h, 4); head[8] = 8; head[9] = 2;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', head), chunk('IDAT', zlib.deflateSync(raw, { level: 6 })), chunk('IEND', Buffer.alloc(0))]);
}
export function decodePng(buf) {
  let at = 8, w = 0, h = 0, type = 0;
  const parts = [];
  while (at < buf.length) {
    const len = buf.readUInt32BE(at), name = buf.toString('latin1', at + 4, at + 8), data = buf.subarray(at + 8, at + 8 + len);
    if (name === 'IHDR') { w = data.readUInt32BE(0); h = data.readUInt32BE(4); type = data[9]; if (data[8] !== 8 || data[12] !== 0) throw new Error('png kind'); }
    if (name === 'IDAT') parts.push(data);
    at += 12 + len;
  }
  const bpp = type === 6 ? 4 : type === 2 ? 3 : (() => { throw new Error('png colour type ' + type); })();
  const raw = zlib.inflateSync(Buffer.concat(parts)), stride = w * bpp, out = new Uint8Array(w * h * 4), row = new Uint8Array(stride), prev = new Uint8Array(stride);
  for (let y = 0; y < h; y++) {
    const f = raw[y * (stride + 1)], src = y * (stride + 1) + 1;
    for (let x = 0; x < stride; x++) {
      const a = x >= bpp ? row[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
      let p = 0;
      if (f === 1) p = a; else if (f === 2) p = b; else if (f === 3) p = (a + b) >> 1;
      else if (f === 4) { const q = a + b - c, pa = Math.abs(q - a), pb = Math.abs(q - b), pc = Math.abs(q - c); p = pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
      row[x] = (raw[src + x] + p) & 255;
    }
    for (let x = 0; x < w; x++) { out[(y * w + x) * 4] = row[x * bpp]; out[(y * w + x) * 4 + 1] = row[x * bpp + 1]; out[(y * w + x) * 4 + 2] = row[x * bpp + 2]; out[(y * w + x) * 4 + 3] = bpp === 4 ? row[x * bpp + 3] : 255; }
    prev.set(row);
  }
  return { w, h, data: out };
}

export const VIEWS = [0, 45, 90, 135, 180, 225, 270, 315].map(a => [a, 22]);

// A sheet of pictures side by side (for looking at them all at once).
export function sheet(pictures, size, columns) {
  const rows = Math.ceil(pictures.length / columns), W = size * columns, H = size * rows, out = new Uint8Array(W * H * 3).fill(255);
  pictures.forEach((p, i) => { const ox = (i % columns) * size, oy = Math.floor(i / columns) * size; for (let y = 0; y < size; y++) out.set(p.subarray(y * size * 3, (y + 1) * size * 3), ((oy + y) * W + ox) * 3); });
  return { rgb: out, w: W, h: H };
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'))) {
  const [, , objPath, outDir, texPath] = process.argv;
  const model = parseObj(fs.readFileSync(objPath, 'utf8'));
  const texture = texPath ? decodePng(fs.readFileSync(texPath)) : null;
  fs.mkdirSync(outDir, { recursive: true });
  const size = 768, small = [];
  const t0 = Date.now();
  VIEWS.forEach(([az, el], i) => {
    const cam = camera(az, el);
    const clay = render(model, cam, size, null);
    fs.writeFileSync(path.join(outDir, `clay_${i}.png`), encodePng(clay.rgb, size, size));
    fs.writeFileSync(path.join(outDir, `id_${i}.bin`), Buffer.from(clay.id.buffer));
    let shown = clay.rgb;
    if (texture) { const c = render(model, cam, size, texture); fs.writeFileSync(path.join(outDir, `colour_${i}.png`), encodePng(c.rgb, size, size)); shown = c.rgb; }
    const half = new Uint8Array(384 * 384 * 3);
    for (let y = 0; y < 384; y++) for (let x = 0; x < 384; x++) for (let ch = 0; ch < 3; ch++) half[(y * 384 + x) * 3 + ch] = shown[((y * 2) * size + x * 2) * 3 + ch];
    small.push(half);
  });
  const s = sheet(small, 384, 4);
  fs.writeFileSync(path.join(outDir, 'sheet.png'), encodePng(s.rgb, s.w, s.h));
  console.log(`${path.basename(objPath)}: ${model.v.length} vertices, ${model.tri.length} triangles, 8 views in ${Date.now() - t0} ms`);
}
