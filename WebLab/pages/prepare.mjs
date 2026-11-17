import { cp, copyFile, mkdir, writeFile } from 'node:fs/promises';
const output = new URL('./dist/', import.meta.url);
await mkdir(output, { recursive: true });
await cp(new URL('../public/collect/', import.meta.url), new URL('collect/', output), { recursive: true });
await copyFile(new URL('../public/collect/index.html', import.meta.url), new URL('index.html', output));
await copyFile(new URL('./worker.mjs', import.meta.url), new URL('_worker.js', output));
await writeFile(new URL('_routes.json', output), JSON.stringify({ version: 1, include: ['/api/collect/*'], exclude: [] }));
console.log('Prepared Roomsmiths Pages assets and collection service binding.');
