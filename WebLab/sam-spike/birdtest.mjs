import fs from 'node:fs';
import { segment, records, calls } from './sam.mjs';
import { decodePng, encodePng, sheet } from './render.mjs';
import { PARTS } from './bird.mjs';
const size = 768, views = [0, 1, 3, 7], looks = ['colour', 'clay'];
const partOf = JSON.parse(fs.readFileSync('out/bird/parts.json', 'utf8'));
const words = ['bird', 'beak', 'wing', 'tail', 'head', 'leg', 'body'];
const iou = (a, b) => { const x1 = Math.max(a.x1, b.x1), y1 = Math.max(a.y1, b.y1), x2 = Math.min(a.x2, b.x2), y2 = Math.min(a.y2, b.y2); const i = Math.max(0, x2 - x1 + 1) * Math.max(0, y2 - y1 + 1); const u = (a.x2 - a.x1 + 1) * (a.y2 - a.y1 + 1) + (b.x2 - b.x1 + 1) * (b.y2 - b.y1 + 1) - i; return u ? i / u : 0; };
const union = list => list.reduce((a, b) => ({ x1: Math.min(a.x1, b.x1), y1: Math.min(a.y1, b.y1), x2: Math.max(a.x2, b.x2), y2: Math.max(a.y2, b.y2) }));
// Truth per view: each part's box and a point on it.
const truth = {};
for (const v of views) {
  const id = new Int32Array(fs.readFileSync(`out/bird/id_${v}.bin`).buffer.slice(0));
  const t = {};
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const k = id[y * size + x]; if (k < 0) continue;
    for (const name of [partOf[k], 'bird']) {
      const q = t[name] ??= { x1: 1e9, y1: 1e9, x2: -1, y2: -1, n: 0, sx: 0, sy: 0, px: [] };
      q.x1 = Math.min(q.x1, x); q.y1 = Math.min(q.y1, y); q.x2 = Math.max(q.x2, x); q.y2 = Math.max(q.y2, y); q.n++; q.sx += x; q.sy += y;
      if (name !== 'bird' && (x + y * 3) % 7 === 0) q.px.push([x, y]);
    }
  }
  for (const [name, q] of Object.entries(t)) if (name !== 'bird') {
    // The wings are two things: take the point on the bigger visible group near its own middle.
    const cx = q.sx / q.n, cy = q.sy / q.n; let best = q.px[0], bd = 1e18;
    for (const p of q.px) { const d = (p[0] - cx) ** 2 + (p[1] - cy) ** 2; if (d < bd) { bd = d; best = p; } }
    q.point = best; delete q.px;
  }
  truth[v] = t;
}
const jobs = [], raw = {};
const run = async (key, png, text) => { const r = await segment(png, text); raw[key] = r.text; if (r.status !== 200) console.log('ERR', key, r.status, JSON.stringify(r.raw).slice(0, 200)); return { recs: records(r.text), ms: r.ms }; };
const pngs = {}; for (const l of looks) for (const v of views) pngs[l + v] = fs.readFileSync(`out/bird/${l}_${v}.png`);
const results = {};
const P = ([x, y]) => `<|point;x=${x};y=${y};w=${size};h=${size}|>`;
for (const l of looks) for (const v of views) {
  for (const w of words) jobs.push(async () => { results[`word|${l}|${v}|${w}`] = await run(`word|${l}|${v}|${w}`, pngs[l + v], w); });
  const t = truth[v], names = PARTS.filter(p => t[p] && t[p].n > 120);
  jobs.push(async () => { results[`point|${l}|${v}`] = { names, ...(await run(`point|${l}|${v}`, pngs[l + v], '<0f>' + names.map((p, i) => `${i + 1}${P(t[p].point)}`).join(','))) }; });
  jobs.push(async () => { results[`pointneg|${l}|${v}`] = { names, ...(await run(`pointneg|${l}|${v}`, pngs[l + v], '<0f>' + names.map((p, i) => `${i + 1}${P(t[p].point)}` + (p === 'body' || !t.body ? '' : '-' + P(t.body.point))).join(','))) }; });
}
const t0 = Date.now();
for (let i = 0; i < jobs.length; i += 8) await Promise.all(jobs.slice(i, i + 8).map(j => j()));
console.log(`${jobs.length} requests in ${((Date.now() - t0) / 1000).toFixed(1)} s (8 at a time)`);
fs.writeFileSync('out/bird/raw.json', JSON.stringify(raw));
fs.writeFileSync('out/bird/truth.json', JSON.stringify(truth));

// ---- words: does the word's box lie on the part?
const table = {};
for (const l of looks) {
  console.log(`\nWORDS on ${l}: best IoU of a returned box with the part's true box (count returned; W = it returned the whole bird)`);
  console.log('view'.padEnd(6) + words.map(w => w.padEnd(13)).join(''));
  for (const v of views) {
    let line = ('#' + v).padEnd(6);
    for (const w of words) {
      const r = results[`word|${l}|${v}|${w}`], t = truth[v][w];
      if (!t) { line += '(hidden)'.padEnd(13); continue; }
      if (!r.recs.length) { line += '-'.padEnd(13); (table[`${l}|${w}`] ??= []).push(0); continue; }
      const boxes = r.recs.map(x => x.box);
      const best = Math.max(...boxes.map(b => iou(b, t)), iou(union(boxes), t));
      const whole = w !== 'bird' && Math.max(...boxes.map(b => iou(b, truth[v].bird))) > .8;
      line += `${best.toFixed(2)} x${boxes.length}${whole ? ' W' : ''}`.padEnd(13);
      (table[`${l}|${w}`] ??= []).push(best);
    }
    console.log(line);
  }
}
for (const kind of ['point', 'pointneg']) for (const l of looks) {
  console.log(`\n${kind === 'point' ? 'ONE POINT per part' : 'POINT on the part + a NO-point on the body'} on ${l}: IoU of the returned box with the part's true box (W = whole bird)`);
  for (const v of views) {
    const r = results[`${kind}|${l}|${v}`];
    console.log(('#' + v).padEnd(6) + r.names.map((p, i) => { const rec = r.recs.find(x => x.id === i + 1); if (!rec) return `${p} -`.padEnd(15); const q = iou(rec.box, truth[v][p]); (table[`${kind}|${l}|${p}`] ??= []).push(q); return `${p} ${q.toFixed(2)}${p !== 'body' && iou(rec.box, truth[v].bird) > .8 ? ' W' : ''}`.padEnd(15); }).join(''));
  }
}
console.log('\nMEANS'); for (const [k, list] of Object.entries(table)) console.log(k.padEnd(28), (list.reduce((a, b) => a + b, 0) / list.length).toFixed(2), 'hits(IoU>=.5)', list.filter(q => q >= .5).length + '/' + list.length);

// ---- pictures: the returned boxes drawn on view 1.
const PAL = { bird: [150, 150, 150], beak: [255, 120, 0], wing: [0, 120, 255], tail: [0, 170, 150], head: [200, 0, 200], leg: [120, 90, 40], body: [230, 0, 0] };
function rect(rgb, b, c) { for (let t = 0; t < 3; t++) { for (let x = b.x1; x <= b.x2; x++) for (const y of [b.y1 + t, b.y2 - t]) if (x >= 0 && x < size && y >= 0 && y < size) rgb.set(c, (y * size + x) * 3); for (let y = b.y1; y <= b.y2; y++) for (const x of [b.x1 + t, b.x2 - t]) if (x >= 0 && x < size && y >= 0 && y < size) rgb.set(c, (y * size + x) * 3); } }
const toRgb = png => { const d = decodePng(png), o = new Uint8Array(size * size * 3); for (let i = 0; i < size * size; i++) { o[i * 3] = d.data[i * 4]; o[i * 3 + 1] = d.data[i * 4 + 1]; o[i * 3 + 2] = d.data[i * 4 + 2]; } return o; };
const pics = [];
for (const l of looks) {
  const a = toRgb(pngs[l + 1]);
  for (const w of words.slice(1)) for (const rec of results[`word|${l}|1|${w}`].recs) rect(a, rec.box, PAL[w]);
  pics.push(a);
  const b = toRgb(pngs[l + 1]);
  const r = results[`point|${l}|1`]; r.names.forEach((p, i) => { const rec = r.recs.find(x => x.id === i + 1); if (rec) rect(b, rec.box, PAL[p]); const pt = truth[1][p].point; rect(b, { x1: pt[0] - 5, y1: pt[1] - 5, x2: pt[0] + 5, y2: pt[1] + 5 }, PAL[p]); });
  pics.push(b);
}
const s = sheet(pics, size, 2);
fs.writeFileSync('out/bird/result_boxes.png', encodePng(s.rgb, s.w, s.h));
console.log('\ncalls this run', calls);
