// A stand-in bird built from simple shapes, each triangle knowing its part: the truth to score SAM against.
export const PARTS = ['body', 'head', 'beak', 'wing', 'tail', 'leg'];
const COLOURS = { body: [238, 236, 230], head: [34, 34, 40], beak: [222, 150, 40], wing: [38, 72, 160], tail: [24, 70, 84], leg: [120, 110, 100] };

export function bird() {
  const v = [], tri = [], part = [];
  const rot = (p, deg) => { const a = deg * Math.PI / 180, c = Math.cos(a), s = Math.sin(a); return [p[0] * c - p[1] * s, p[0] * s + p[1] * c, p[2]]; };   // about z
  function ellipsoid(name, centre, radii, tilt = 0, rings = 18, segs = 28) {
    const base = v.length;
    for (let i = 0; i <= rings; i++) for (let j = 0; j < segs; j++) {
      const t = Math.PI * i / rings, f = 2 * Math.PI * j / segs;
      const p = rot([radii[0] * Math.sin(t) * Math.cos(f), radii[1] * Math.cos(t), radii[2] * Math.sin(t) * Math.sin(f)], tilt);
      v.push([p[0] + centre[0], p[1] + centre[1], p[2] + centre[2]]);
    }
    for (let i = 0; i < rings; i++) for (let j = 0; j < segs; j++) {
      const a = base + i * segs + j, b = base + i * segs + (j + 1) % segs, c = a + segs, d = b + segs;
      tri.push([a, c, b], [b, c, d]); part.push(name, name);
    }
  }
  function cone(name, from, to, r0, r1, segs = 20) {
    const base = v.length, axis = [to[0] - from[0], to[1] - from[1], to[2] - from[2]];
    const len = Math.hypot(...axis), ax = axis.map(q => q / len);
    const side = Math.abs(ax[1]) < .9 ? [0, 1, 0] : [1, 0, 0];
    const u = norm(cross(ax, side)), w = cross(ax, u);
    for (const [at, r] of [[from, r0], [to, r1]]) for (let j = 0; j < segs; j++) {
      const f = 2 * Math.PI * j / segs;
      v.push([at[0] + (u[0] * Math.cos(f) + w[0] * Math.sin(f)) * r, at[1] + (u[1] * Math.cos(f) + w[1] * Math.sin(f)) * r, at[2] + (u[2] * Math.cos(f) + w[2] * Math.sin(f)) * r]);
    }
    for (let j = 0; j < segs; j++) { const a = base + j, b = base + (j + 1) % segs; tri.push([a, b, a + segs], [b, b + segs, a + segs]); part.push(name, name); }
  }
  ellipsoid('body', [0, 0, 0], [.30, .19, .17], 12);
  ellipsoid('head', [.27, .20, 0], [.125, .12, .115]);
  cone('beak', [.37, .205, 0], [.53, .18, 0], .045, .004);
  ellipsoid('wing', [-.04, .035, .165], [.23, .115, .03], 4);
  ellipsoid('wing', [-.04, .035, -.165], [.23, .115, .03], 4);
  ellipsoid('tail', [-.43, .05, 0], [.24, .03, .075], -14);
  for (const z of [.07, -.07]) { cone('leg', [.03, -.15, z], [.03, -.37, z], .014, .012); ellipsoid('leg', [.06, -.375, z], [.06, .012, .03]); }
  // Unit size, centred; normals per vertex.
  const lo = [1e9, 1e9, 1e9], hi = [-1e9, -1e9, -1e9];
  for (const p of v) for (let k = 0; k < 3; k++) { lo[k] = Math.min(lo[k], p[k]); hi[k] = Math.max(hi[k], p[k]); }
  const size = Math.max(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]);
  for (const p of v) for (let k = 0; k < 3; k++) p[k] = (p[k] - (lo[k] + hi[k]) / 2) / size;
  const n = v.map(() => [0, 0, 0]);
  for (const t of tri) { const f = cross(sub(v[t[1]], v[t[0]]), sub(v[t[2]], v[t[0]])); for (const i of t) for (let k = 0; k < 3; k++) n[i][k] += f[k]; }
  for (let i = 0; i < n.length; i++) n[i] = norm(n[i]);
  return { v, vt: [], tri, triUv: tri.map(() => [-1, -1, -1]), n, part, triColour: part.map(p => COLOURS[p]) };
}
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const norm = a => { const m = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / m, a[1] / m, a[2] / m]; };
