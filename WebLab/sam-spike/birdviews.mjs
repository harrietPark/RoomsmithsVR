import fs from 'node:fs';
import { camera, render, encodePng, sheet, VIEWS } from './render.mjs';
import { bird } from './bird.mjs';
const model = bird(), size = 768;
fs.mkdirSync('out/bird', { recursive: true });
const small = [];
for (const look of ['colour', 'clay']) VIEWS.forEach(([az, el], i) => {
  const r = render(model, camera(az, el), size, look === 'colour' ? { parts: true } : null);
  fs.writeFileSync(`out/bird/${look}_${i}.png`, encodePng(r.rgb, size, size));
  if (look === 'clay') fs.writeFileSync(`out/bird/id_${i}.bin`, Buffer.from(r.id.buffer));
  const half = new Uint8Array(384 * 384 * 3);
  for (let y = 0; y < 384; y++) for (let x = 0; x < 384; x++) for (let ch = 0; ch < 3; ch++) half[(y * 384 + x) * 3 + ch] = r.rgb[((y * 2) * size + x * 2) * 3 + ch];
  if (i % 2 === 0 || i === 1) small.push(half);
});
const s = sheet(small.slice(0, 4).concat(small.slice(5, 9)), 384, 4);
fs.writeFileSync('out/bird/sheet.png', encodePng(s.rgb, s.w, s.h));
fs.writeFileSync('out/bird/parts.json', JSON.stringify(model.part));
console.log('bird', model.v.length, 'vertices', model.tri.length, 'triangles');
