// SPDX-License-Identifier: GPL-2.0-or-later

import { spawn, spawnSync } from 'node:child_process';
import { cpSync, mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { chromium, type Browser, type Locator, type Page } from 'playwright';
import { mirrorLibrary } from './mirror-library.ts';
import { seedDemo } from './seed-demo.ts';

const ROOT = join(import.meta.dirname, '..', '..', '..');
const CONFIG = process.env.CONFIG ?? 'Debug';
const HOST_DIR = resolve(process.env.TRIPT_HOST_DIR ?? join(ROOT, 'dist', CONFIG));
const OUT = join(ROOT, 'docs', 'shots');
const WORK = join(tmpdir(), 'tript-shots');
const VIEWPORT = { width: 1440, height: 900 };
const NPM = process.platform === 'win32' ? 'npm.cmd' : 'npm';
const HOST = process.platform === 'win32' ? join(HOST_DIR, 'Tript.App.exe') : join(HOST_DIR, 'Tript.App');
const PORTS = { ui: 18892, content: 18893, control: 18894 };
const only = process.argv.slice(2);
const LIBRARY = process.env.SHOTS_LIBRARY;
const SETTINGS_SOURCE = process.env.SHOTS_SETTINGS
  ?? join(process.env.APPDATA ?? join(process.env.HOME ?? '', '.config'), 'Tript', 'settings.json');
const FOCUS = process.env.SHOTS_SESSION ?? 'Ranked with the squad';
const HERO_AT = (process.env.SHOTS_HERO_AT ?? '100').split(',').map(Number);

function buildFrontend(): void {
  const web = join(import.meta.dirname, '..');
  const built = spawnSync(NPM, ['run', 'build'], {
    cwd: web,
    stdio: 'inherit',
    shell: process.platform === 'win32',
  });
  if (built.status !== 0) {
    throw new Error('frontend build failed, captures would show the previous bundle');
  }
  const served = join(HOST_DIR, 'dist');
  rmSync(served, { recursive: true, force: true });
  cpSync(join(web, 'dist'), served, { recursive: true });
}

type Host = { url: string; stop: () => void };

function startHost(contentRoot: string, settings: string): Promise<Host> {
  const child = spawn(
    HOST,
    [
      '--fake-recorder',
      '--disable-updater',
      '--settings-path', settings,
      '--content-root', contentRoot,
      '--log-dir', join(WORK, 'logs'),
      '--ui-port', String(PORTS.ui),
      '--content-port', String(PORTS.content),
      '--control-port', String(PORTS.control),
    ],
    {
      cwd: HOST_DIR,
      env: process.platform === 'win32'
        ? process.env
        : { ...process.env, DOTNET_ROOT: process.env.DOTNET_ROOT ?? `${process.env.HOME}/.dotnet` },
    },
  );
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

async function openApp(browser: Browser, host: Host): Promise<Page> {
  const page = await browser.newPage({ viewport: VIEWPORT, deviceScaleFactor: 2 });
  await page.route('http://localhost:8893/**', (route) =>
    route.continue({ url: route.request().url().replace(':8893/', `:${PORTS.content}/`) }));
  await page.routeWebSocket('ws://localhost:8894/**', (socket) => {
    const upstream = new WebSocket(socket.url().replace(':8894/', `:${PORTS.control}/`));
    const pending: string[] = [];
    upstream.onopen = () => {
      for (const message of pending.splice(0)) upstream.send(message);
    };
    upstream.onmessage = (event) => socket.send(String(event.data));
    upstream.onclose = () => socket.close();
    socket.onMessage((message) => {
      if (upstream.readyState === WebSocket.OPEN) upstream.send(String(message));
      else pending.push(String(message));
    });
    socket.onClose(() => upstream.close());
  });
  await page.goto(host.url, { waitUntil: 'networkidle' });
  await page.getByRole('radiogroup', { name: 'Show' }).waitFor();
  await page.waitForTimeout(1500);
  return page;
}

function wanted(name: string): boolean {
  return only.length === 0 || only.some((prefix) => name.startsWith(prefix));
}

async function shoot(page: Page, name: string, target?: Locator, pad = 12): Promise<void> {
  if (!wanted(name)) return;
  await page.waitForTimeout(400);
  const path = join(OUT, `${name}.png`);
  if (target) {
    const box = await target.boundingBox();
    if (!box) throw new Error(`${name}: target is not visible`);
    const x = Math.max(0, box.x - pad);
    const y = Math.max(0, box.y - pad);
    await page.screenshot({
      path,
      clip: {
        x,
        y,
        width: Math.min(VIEWPORT.width - x, box.width + pad * 2),
        height: Math.min(VIEWPORT.height - y, box.height + pad * 2),
      },
    });
  } else {
    await page.screenshot({ path });
  }
  console.log(`  ${name}.png`);
}

async function annotate(page: Page, marks: { target: Locator; label: string; end?: boolean }[]): Promise<void> {
  for (const mark of marks) {
    const box = await mark.target.first().boundingBox({ timeout: 2000 }).catch(() => null);
    if (!box) continue;
    await page.evaluate(
      ({ x, y, label }) => {
        const badge = document.createElement('div');
        badge.className = 'docs-callout';
        badge.textContent = label;
        Object.assign(badge.style, {
          position: 'fixed',
          left: `${x}px`,
          top: `${y}px`,
          width: '26px',
          height: '26px',
          borderRadius: '50%',
          background: '#ffd23f',
          color: '#111',
          font: '700 14px/26px system-ui, sans-serif',
          textAlign: 'center',
          boxShadow: '0 0 0 3px rgba(0,0,0,0.55)',
          zIndex: '99999',
          pointerEvents: 'none',
        });
        document.body.appendChild(badge);
      },
      {
        x: mark.end ? box.x + box.width - 13 : Math.max(2, box.x - 13),
        y: Math.max(2, box.y - 13),
        label: mark.label,
      },
    );
  }
}

async function clearAnnotations(page: Page): Promise<void> {
  await page.evaluate(() => document.querySelectorAll('.docs-callout').forEach((node) => node.remove()));
}

const button = (page: Page, name: string | RegExp) => page.getByRole('button', { name });
const toast = (page: Page, text: string | RegExp) => page.locator('.toast-stack > *').filter({ hasText: text }).first();

async function blur(page: Page): Promise<void> {
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
}

async function seek(page: Page, seconds: number): Promise<void> {
  await page.evaluate((to) => {
    const video = document.querySelector('video');
    if (!video) return;
    video.pause();
    video.currentTime = to;
  }, seconds);
  await page.waitForTimeout(500);
  await blur(page);
}

async function nav(page: Page, name: string): Promise<void> {
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('button', { name, exact: true }).click();
  await page.waitForTimeout(500);
}

async function settingsTab(page: Page, name: string): Promise<void> {
  await nav(page, 'Settings');
  await page.getByRole('tab', { name, exact: true }).click();
  await page.waitForTimeout(400);
}

async function firstRun(browser: Browser): Promise<void> {
  const empty = join(WORK, 'empty-root');
  rmSync(empty, { recursive: true, force: true });
  mkdirSync(empty, { recursive: true });
  const settings = join(WORK, 'first-run-settings.json');
  rmSync(settings, { force: true });
  const host = await startHost(empty, settings);
  try {
    const page = await openApp(browser, host);
    await shoot(page, 'first-run-empty-library');
    await page.close();
  } finally {
    host.stop();
    await new Promise((resolve) => setTimeout(resolve, 1500));
  }
}

async function library(page: Page): Promise<void> {
  await shoot(page, 'library-all');
  const cards = page.locator('.content-card').filter({ visible: true });
  await annotate(page, [
    { target: page.getByRole('radiogroup', { name: 'Show' }), label: '1' },
    { target: page.locator('.content-card').filter({ hasText: /Highlights[- ]only/ }).first(), label: '2' },
    { target: cards.filter({ hasText: 'Recording' }).filter({ hasNotText: /Highlights[- ]only/ }).first(), label: '3' },
    { target: cards.filter({ hasText: 'Highlight' }).filter({ hasNotText: 'Recording' }).first(), label: '4' },
    { target: button(page, 'Select'), label: '5' },
  ]);
  await shoot(page, 'library-annotated');
  await clearAnnotations(page);

  await button(page, 'Select').click();
  const checks = page.getByRole('checkbox', { name: /^Select / }).filter({ visible: true });
  for (const index of [1, 3]) {
    await checks.nth(index).focus();
    await page.keyboard.press('Space');
  }
  await page.mouse.move(1430, 890);
  await shoot(page, 'library-select');
  await button(page, 'Done').click();

  await page.locator('.content-card-delete').filter({ visible: true }).first().click({ force: true });
  const dialog = page.locator('.confirm-dialog-panel');
  await dialog.waitFor();
  await shoot(page, 'delete-dialog', dialog);
  await page.keyboard.press('Escape');

  await nav(page, 'Sessions');
  await shoot(page, 'sessions');
  await nav(page, 'Library');
}

async function player(page: Page): Promise<void> {
  await page.getByRole('textbox', { name: 'Search' }).fill(FOCUS);
  await page.waitForTimeout(600);
  await page.getByRole('button', { name: `Open ${FOCUS}`, exact: true })
    .filter({ hasText: 'Recording' }).filter({ visible: true }).first().click();
  await page.locator('video').waitFor();
  await page.waitForTimeout(1000);
  for (const at of HERO_AT) {
    await seek(page, at);
    await page.waitForTimeout(800);
    await shoot(page, HERO_AT.length === 1 ? 'hero' : `hero-${at}`);
  }

  await seek(page, 70);
  await shoot(page, 'player-overview');

  await annotate(page, [
    { target: button(page, 'Back'), label: '1' },
    { target: button(page, 'Create highlights'), label: '2' },
    { target: button(page, /^Review highlights/), label: '3' },
    { target: button(page, 'Previous item'), label: '4' },
    { target: page.getByRole('slider', { name: 'Recording position' }), label: '5', end: true },
    { target: page.locator('.timeline-zoomed'), label: '6' },
    { target: button(page, 'Add a bookmark where you are'), label: '7' },
    { target: page.getByRole('complementary', { name: 'Playlist and bookmarks' }), label: '8' },
  ]);
  await shoot(page, 'player-annotated');
  await clearAnnotations(page);

  const header = page.locator('main').locator('button', { hasText: 'Back' }).locator('xpath=..');
  await shoot(page, 'player-session-header', header, 6);

  await shoot(page, 'player-playlist-panel', page.getByRole('complementary', { name: 'Playlist and bookmarks' }), 4);

  await page.getByRole('tab', { name: 'Bookmarks' }).click();
  await page.waitForTimeout(300);
  await shoot(page, 'bookmarks-panel', page.getByRole('complementary', { name: 'Playlist and bookmarks' }), 4);
  await page.getByRole('tab', { name: 'Playlist' }).click();

  await seek(page, 20);
  await page.keyboard.press('m');
  await seek(page, 95);
  await page.keyboard.press('i');
  await seek(page, 110);
  await page.keyboard.press('o');
  await seek(page, 100);
  await shoot(page, 'player-regions');

  await button(page, 'Open clip dialog').click();
  const clipDialog = page.getByRole('dialog', { name: 'Create clip' });
  await clipDialog.waitFor();
  await shoot(page, 'clip-dialog', clipDialog.locator('.clip-dialog-panel'), 4);
  await button(page, 'Close clip dialog').click();
  await page.waitForTimeout(300);

  await button(page, 'Create clips').click();
  const created = toast(page, /^Created/);
  await created.waitFor({ timeout: 60_000 });
  await shoot(page, 'clip-created-toast', created, 8);
  await page.getByTestId('recorder-bar').filter({ hasText: /Creating clips/ }).waitFor({ state: 'detached', timeout: 120_000 });

  await button(page, /^Review highlights/).click();
  await page.waitForTimeout(1200);
  await shoot(page, 'review-highlights-grid');

  await page.locator('.session-clips-grid .content-card').first().click();
  await page.locator('video').waitFor();
  await page.waitForTimeout(1000);
  await seek(page, 4);
  await shoot(page, 'player-review-playlist');

  await page.keyboard.press('Delete');
  const restore = toast(page, /trash/i);
  await restore.waitFor({ timeout: 10_000 });
  await shoot(page, 'delete-restore-toast', restore, 8);

  await button(page, 'Back').click();
  await page.waitForTimeout(400);
  await nav(page, 'Library');
  await page.getByRole('textbox', { name: 'Search' }).fill('');
}

async function trash(page: Page): Promise<void> {
  await page.getByRole('radio', { name: /^Trash/ }).click();
  await page.waitForTimeout(800);
  await shoot(page, 'library-trash');
  await page.getByRole('radio', { name: 'All' }).click();
}

async function settingsPages(page: Page): Promise<void> {
  for (const [tab, name] of [
    ['General', 'settings-general'],
    ['Recording', 'settings-recording'],
    ['Highlights', 'settings-highlights'],
    ['Storage', 'settings-storage'],
    ['Audio', 'settings-audio'],
    ['Capture', 'settings-capture'],
    ['Games', 'settings-games'],
    ['Hotkeys', 'settings-hotkeys'],
  ]) {
    await settingsTab(page, tab);
    await shoot(page, name);
  }

  await settingsTab(page, 'Games');
  await button(page, 'Add custom game').click();
  await page.waitForTimeout(400);
  await shoot(page, 'custom-game-editor');

  await nav(page, 'Streamer');
  await shoot(page, 'streamer');
  await nav(page, 'Library');
}

async function recording(page: Page): Promise<void> {
  await button(page, 'Record').click();
  await page.waitForTimeout(3500);
  await shoot(page, 'recorder-bar-recording', page.getByTestId('recorder-bar'), 8);
  await shoot(page, 'library-recording');
  await button(page, 'Stop').click();
  await page.waitForTimeout(1500);
}

async function main(): Promise<void> {
  if (only.length === 0) rmSync(OUT, { recursive: true, force: true });
  mkdirSync(OUT, { recursive: true });
  mkdirSync(WORK, { recursive: true });

  if (!process.env.SKIP_BUILD) buildFrontend();
  const browser = await chromium.launch({
    channel: process.env.SHOTS_BROWSER,
    headless: Boolean(process.env.SHOTS_HEADLESS),
  });
  try {
    if (wanted('first-run')) await firstRun(browser);

    const settings = join(WORK, 'library-settings.json');
    rmSync(settings, { force: true });
    let root: string;
    if (LIBRARY) {
      root = mirrorLibrary(LIBRARY, SETTINGS_SOURCE, settings);
    } else {
      root = join(WORK, 'demo-root');
      console.log('seeding demo library...');
      seedDemo(root);
    }
    const host = await startHost(root, settings);
    try {
      const page = await openApp(browser, host);
      await library(page);
      await player(page);
      await trash(page);
      await settingsPages(page);
      await recording(page);
      await page.close();
    } finally {
      host.stop();
    }
  } finally {
    await browser.close();
  }
  console.log(`\nwrote ${OUT}`);
}

await main();
