// SPDX-License-Identifier: GPL-2.0-or-later

import { createServer } from 'node:http';
import { readFileSync, statSync, writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, extname, basename } from 'node:path';
import { chromium } from 'playwright';

const [videoPath, audioPath] = process.argv.slice(2);
if (!videoPath || !audioPath) {
  console.error('usage: measure-av-sync.ts <video> <sidecar-audio>');
  process.exit(2);
}

const root = mkdtempSync(join(tmpdir(), 'av-sync-'));
const page = `<!doctype html><meta charset="utf-8">
<video id="v" src="${basename(videoPath)}" muted playsinline></video>
<audio id="a" src="${basename(audioPath)}" preload="auto"></audio>
<script>
const v = document.getElementById('v'), a = document.getElementById('a');
window.__drift = [];
v.addEventListener('play', () => a.play());
v.addEventListener('pause', () => a.pause());
v.addEventListener('seeking', () => a.pause());
v.addEventListener('seeked', () => { a.currentTime = v.currentTime; if (!v.paused) a.play(); });
v.addEventListener('ratechange', () => { a.playbackRate = v.playbackRate; });
setInterval(() => {
  if (v.paused || v.seeking) return;
  const d = a.currentTime - v.currentTime;
  window.__drift.push(d);
  if (Math.abs(d) > 0.05) a.currentTime = v.currentTime;
}, 100);
window.__ready = () => v.readyState >= 3 && a.readyState >= 3;
</script>`;
writeFileSync(join(root, 'index.html'), page);

const TYPES: Record<string, string> = { '.html': 'text/html', '.mp4': 'video/mp4', '.m4a': 'audio/mp4' };
const sources: Record<string, string> = {
  [`/${basename(videoPath)}`]: videoPath,
  [`/${basename(audioPath)}`]: audioPath,
  '/index.html': join(root, 'index.html'),
};

const server = createServer((request, response) => {
  const url = (request.url || '/').split('?')[0];
  const file = sources[url === '/' ? '/index.html' : url];
  if (!file) {
    response.writeHead(404).end();
    return;
  }
  const size = statSync(file).size;
  const type = TYPES[extname(file)] ?? 'application/octet-stream';
  const range = request.headers.range;
  if (!range) {
    response.writeHead(200, { 'Content-Type': type, 'Accept-Ranges': 'bytes', 'Content-Length': size });
    response.end(readFileSync(file));
    return;
  }
  const [from, to] = range.replace('bytes=', '').split('-');
  const start = Number(from);
  const end = to ? Number(to) : size - 1;
  response.writeHead(206, {
    'Content-Type': type,
    'Accept-Ranges': 'bytes',
    'Content-Range': `bytes ${start}-${end}/${size}`,
    'Content-Length': end - start + 1,
  });
  response.end(readFileSync(file).subarray(start, end + 1));
});
await new Promise<void>((resolve) => server.listen(8097, resolve));

const browser = await chromium.launch({ args: ['--autoplay-policy=no-user-gesture-required'] });
const tab = await browser.newPage();
await tab.goto('http://127.0.0.1:8097/', { waitUntil: 'load' });
await tab.waitForFunction('window.__ready && window.__ready()', null, { timeout: 20000 });

const ms = (value: number) => `${(value * 1000).toFixed(1)} ms`;

const seekDrift: number[] = [];
for (const target of [3, 17, 8, 25, 1, 29, 12, 20, 5, 27]) {
  seekDrift.push(
    await tab.evaluate(async (time) => {
      const v = document.getElementById('v') as HTMLVideoElement;
      const a = document.getElementById('a') as HTMLAudioElement;
      v.currentTime = time;
      await new Promise((resolve) => v.addEventListener('seeked', resolve, { once: true }));
      await new Promise((resolve) => setTimeout(resolve, 60));
      return a.currentTime - v.currentTime;
    }, target),
  );
}
console.log(`hard seeks (${seekDrift.length}): worst ${ms(Math.max(...seekDrift.map(Math.abs)))}`);

await tab.evaluate(() => (document.getElementById('v') as HTMLVideoElement).play());
await tab.waitForTimeout(400);
const afterDrag = await tab.evaluate(async () => {
  const v = document.getElementById('v') as HTMLVideoElement;
  const a = document.getElementById('a') as HTMLAudioElement;
  for (let step = 0; step < 25; step += 1) {
    v.currentTime = 2 + step * 0.9;
    await new Promise((resolve) => setTimeout(resolve, 40));
  }
  await new Promise((resolve) => setTimeout(resolve, 300));
  return a.currentTime - v.currentTime;
});
console.log(`scrub while playing, after release: ${ms(afterDrag)}`);

await tab.evaluate(() => {
  (window as unknown as { __drift: number[] }).__drift = [];
  const v = document.getElementById('v') as HTMLVideoElement;
  v.currentTime = 0;
  void v.play();
});
await tab.waitForTimeout(15000);
const drift: number[] = await tab.evaluate('window.__drift');
const magnitudes = drift.map(Math.abs);
console.log(`sustained playback (${drift.length} samples over 15s):`);
console.log(`  worst ${ms(Math.max(...magnitudes))}, mean ${ms(magnitudes.reduce((a, b) => a + b, 0) / drift.length)}`);
console.log(`  corrections fired: ${magnitudes.filter((value) => value > 0.05).length}`);

await browser.close();
server.close();
