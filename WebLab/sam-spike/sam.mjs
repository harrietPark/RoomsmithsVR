// Spike: one SAM 3.1 request (Meta Model API). The key is read from the project's LocalSettings and never printed.
import fs from 'node:fs';

const KEY_FILE = new URL('../../LocalSettings/meta-api-key.txt', import.meta.url);
let key = null;
export let calls = 0;

// text: a short noun phrase, or object prompts (<0f>1<|point;...|>). Returns { text, ms, status, raw }.
export async function segment(pngBuffer, text, { confidence = true, encoding = null } = {}) {
  key ??= fs.readFileSync(KEY_FILE, 'utf8').trim();
  const metadata = {};
  if (confidence) metadata.include_confidence = 'true';
  if (encoding) metadata.mask_encoding = encoding;
  const body = {
    model: 'sam-3.1', store: false, metadata,
    input: [{ type: 'message', role: 'user', content: [
      { type: 'input_text', text },
      { type: 'input_image', image_url: 'data:image/png;base64,' + pngBuffer.toString('base64') }] }]
  };
  const t0 = Date.now();
  calls++;
  const reply = await fetch('https://api.meta.ai/v1/responses', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + key }, body: JSON.stringify(body)
  });
  const rawText = await reply.text();
  const ms = Date.now() - t0;
  let raw = null, out = '';
  try {
    raw = JSON.parse(rawText);
    for (const item of raw.output ?? []) for (const c of item.content ?? []) if (typeof c.text === 'string') out += c.text;
    if (!out && typeof raw.output_text === 'string') out = raw.output_text;
  } catch { /* not JSON */ }
  return { text: out, ms, status: reply.status, raw: raw ?? rawText };
}

// The records of one image line: [{ id, box: {x1,y1,x2,y2,w,h,c}, mask: {h, w, enc, payload, c} }]
export function records(line) {
  const out = [];
  const re = /(\d+)<\|box;([^|]*)\|><\|mask;([^]*?)\|>(?=,\d+<\|box|$)/g;
  const body = line.trim().replace(/^<\d+f>/, '');
  let m;
  while ((m = re.exec(body)) !== null) {
    const box = {};
    for (const kv of m[2].split(';')) { const [k, v] = kv.split('='); box[k] = +v; }
    const at = m[3].indexOf('data=');
    const head = m[3].slice(0, at), data = m[3].slice(at + 5);
    const mask = {};
    for (const kv of head.split(';')) { if (!kv) continue; const [k, v] = kv.split('='); mask[k] = +v; }
    const c1 = data.indexOf(','), c2 = data.indexOf(',', c1 + 1);
    mask.h = +data.slice(0, c1); mask.w = +data.slice(c1 + 1, c2); mask.enc = data[c2 + 1]; mask.payload = data.slice(c2 + 2);
    out.push({ id: +m[1], box, mask });
  }
  return out;
}
