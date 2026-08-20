// SPDX-License-Identifier: GPL-2.0-or-later
//
// Capture every UI surface as a PNG, so UI work is looked at before it is handed over rather than
// after (docs/design-system.md §6). Boots the headless host, reads the launch key off its READY
// line — the UI refuses every request without it — walks the app, and writes docs/shots/.
//
//   cd src/Tript.Web && npm run shots
//
// Needs a built app in dist/<config>: `make shell` (or `make linux`) first.

import { spawn } from 'node:child_process';
import { mkdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { chromium, type Page } from 'playwright';

const ROOT = join(import.meta.dirname, '..', '..', '..');
const CONFIG = process.env.CONFIG ?? 'Debug';
const HOST_DIR = join(ROOT, 'dist', CONFIG);
const OUT = join(ROOT, 'docs', 'shots');
const VIEWPORT = { width: 1440, height: 900 };
/** The width below which the player uses the overlay rather than the full-size route. */
const COMPACT = { width: 1000, height: 800 };

function startHost(): Promise<{ url: string; stop: () => void }> {
  const child = spawn('./Tript.App', ['--fake-recorder'], {
    cwd: HOST_DIR,
    env: { ...process.env, DOTNET_ROOT: process.env.DOTNET_ROOT ?? `${process.env.HOME}/.dotnet` },
  });
  const stop = () => child.kill('SIGINT');
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      stop();
      reject(new Error('host printed no READY line within 30s'));
    }, 30_000);
    let buffered = '';
    child.stdout.on('data', (chunk: Buffer) => {
      buffered += chunk.toString();
      const match = buffered.match(/READY (\S+)/);
      if (match) {
        clearTimeout(timer);
        resolve({ url: match[1], stop });
      }
    });
    child.stderr.on('data', (chunk: Buffer) => process.stderr.write(chunk));
    child.on('exit', (code) => reject(new Error(`host exited early (${code})`)));
  });
}

async function shoot(page: Page, name: string): Promise<void> {
  // Let transitions settle; a half-played hover reads as a rendering bug in the PNG.
  await page.waitForTimeout(350);
  await page.screenshot({ path: join(OUT, `${name}.png`), fullPage: false });
  console.log(`  ${name}.png`);
}

const byName = (page: Page, name: string) => page.getByRole('button', { name });

async function main(): Promise<void> {
  rmSync(OUT, { recursive: true, force: true });
  mkdirSync(OUT, { recursive: true });

  const host = await startHost();
  console.log(`host up: ${host.url.replace(/k=.*/, 'k=<key>')}`);
  const browser = await chromium.launch();

  try {
    const page = await browser.newPage({ viewport: VIEWPORT, deviceScaleFactor: 2 });
    await page.goto(host.url, { waitUntil: 'networkidle' });

    await shoot(page, 'library');

    const card = page.locator('.content-card').first();
    if (await card.count()) {
      await card.hover();
      // The delete action only exists on hover — the state where it collides with the favourite.
      await shoot(page, 'library-card-hover');
      await card.click();
      await page.waitForTimeout(600);
      await shoot(page, 'player');

      const markButton = byName(page, 'Mark segment around the playhead');
      if (await markButton.count()) {
        await markButton.click();
        await shoot(page, 'player-with-segment');
      }
      await page.keyboard.press('Escape');
      await page.waitForTimeout(300);
    } else {
      console.log('  (no content — put an .mp4 in ~/Videos/Tript/sessions to capture the player)');
    }

    await byName(page, 'Trash').click();
    await shoot(page, 'trash');
    await byName(page, 'Settings').click();
    await shoot(page, 'settings');
    await page.close();

    const compact = await browser.newPage({ viewport: COMPACT, deviceScaleFactor: 2 });
    await compact.goto(host.url, { waitUntil: 'networkidle' });
    await shoot(compact, 'library-compact');
    await compact.close();
  } finally {
    await browser.close();
    host.stop();
  }
  console.log(`\nwrote ${OUT}`);
}

await main();
