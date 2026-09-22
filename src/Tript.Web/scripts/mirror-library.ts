// SPDX-License-Identifier: GPL-2.0-or-later

import { copyFileSync, existsSync, linkSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';

const MIRROR_NAME = 'tript-docs-mirror';
const SKIPPED = new Set(['.trash', '.scratch']);

export function mirrorPathFor(library: string): string {
  return join(dirname(resolve(library)), MIRROR_NAME);
}

function mirrorTree(source: string, target: string): number {
  mkdirSync(target, { recursive: true });
  let linked = 0;
  for (const entry of readdirSync(source, { withFileTypes: true })) {
    if (SKIPPED.has(entry.name)) continue;
    const from = join(source, entry.name);
    const to = join(target, entry.name);
    if (entry.isDirectory()) {
      linked += mirrorTree(from, to);
    } else if (entry.name.toLowerCase().endsWith('.mp4')) {
      linkSync(from, to);
      linked += 1;
    } else {
      copyFileSync(from, to);
    }
  }
  return linked;
}

export function mirrorLibrary(library: string, settingsSource: string, settingsTarget: string): string {
  const source = resolve(library);
  const mirror = mirrorPathFor(source);
  if (basename(mirror) !== MIRROR_NAME || mirror === source) {
    throw new Error(`refusing to use ${mirror} as a mirror`);
  }
  rmSync(mirror, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
  const linked = mirrorTree(source, mirror);
  console.log(`mirrored ${source} -> ${mirror} (${linked} videos hardlinked)`);

  const settings = existsSync(settingsSource) ? JSON.parse(readFileSync(settingsSource, 'utf8')) : {};
  settings.recording = { ...(settings.recording ?? {}), outputDirectory: mirror };
  writeFileSync(settingsTarget, JSON.stringify(settings, null, 2));
  return mirror;
}
