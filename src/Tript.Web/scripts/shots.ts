// SPDX-License-Identifier: GPL-2.0-or-later

import { spawn, spawnSync } from 'node:child_process';
import { cpSync, mkdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { chromium, type Page } from 'playwright';

const ROOT = join(import.meta.dirname, '..', '..', '..');
const CONFIG = process.env.CONFIG ?? 'Debug';
const HOST_DIR = join(ROOT, 'dist', CONFIG);
const OUT = join(ROOT, 'docs', 'shots');
const SETTINGS = join(ROOT, 'docs', 'shots-settings.json');
const VIEWPORT = { width: 1440, height: 900 };
const COMPACT = { width: 1000, height: 800 };
const NPM = process.platform === 'win32' ? 'npm.cmd' : 'npm';
const HOST = process.platform === 'win32' ? join(HOST_DIR, 'Tript.App.exe') : './Tript.App';

function buildFrontend(): void {
  const web = join(import.meta.dirname, '..');
  const built = spawnSync(NPM, ['run', 'build'], {
    cwd: web,
    stdio: 'inherit',
    shell: process.platform === 'win32',
  });
  if (built.status !== 0) {
    throw new Error('frontend build failed — captures would show the previous bundle');
  }
  const served = join(HOST_DIR, 'dist');
  rmSync(served, { recursive: true, force: true });
  cpSync(join(web, 'dist'), served, { recursive: true });
}

function startHost(): Promise<{ url: string; stop: () => void }> {
  rmSync(SETTINGS, { force: true });
  const child = spawn(HOST, ['--fake-recorder', '--settings-path', SETTINGS], {
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
  await page.waitForTimeout(350);
  await page.screenshot({ path: join(OUT, `${name}.png`), fullPage: false });
  console.log(`  ${name}.png`);
}

const byName = (page: Page, name: string) => page.getByRole('button', { name });

async function main(): Promise<void> {
  rmSync(OUT, { recursive: true, force: true });
  mkdirSync(OUT, { recursive: true });

  buildFrontend();
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
      await shoot(page, 'library-card-hover');
      await card.click();
      await page.waitForTimeout(600);
      await shoot(page, 'player');

      const markButton = byName(page, 'Mark segment around the playhead');
      if (await markButton.count()) {
        await markButton.click();
        await shoot(page, 'player-with-segment');
      }
      await page.getByRole('button', { name: 'Library', exact: true }).click();
      await page.waitForTimeout(300);
    } else {
      console.log('  (no content — put an .mp4 in ~/Videos/Tript/sessions to capture the player)');
    }

    const trashFilter = page.getByRole('radio', { name: /^Trash/ });
    if (await trashFilter.count()) {
      await trashFilter.click();
      await shoot(page, 'trash');
      await page.getByRole('radio', { name: 'All' }).click();
    }
    await byName(page, 'Settings').click();
    await shoot(page, 'settings');
    await page.close();

    const compact = await browser.newPage({ viewport: COMPACT, deviceScaleFactor: 2 });
    await compact.goto(host.url, { waitUntil: 'networkidle' });
    await shoot(compact, 'library-compact');
    const compactCard = compact.locator('.content-card').first();
    if (await compactCard.count()) {
      await compactCard.click();
      await compact.waitForTimeout(600);
      await shoot(compact, 'player-compact');
    }
    await compact.close();
  } finally {
    await browser.close();
    host.stop();
  }
  console.log(`\nwrote ${OUT}`);
}

await main();
