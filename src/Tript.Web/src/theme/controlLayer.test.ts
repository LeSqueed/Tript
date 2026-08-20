// SPDX-License-Identifier: GPL-2.0-or-later
//
// The control layer is only a layer if views actually use it (docs/design-system.md §4). These
// guards catch the two ways it gets bypassed, both of which happened: a view rendering a bare
// native control beside styled ones, and a view reaching for `.btn` directly — which since the
// variants moved into the layer renders an unstyled button rather than a primary one.

import { readFileSync, readdirSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

const SRC_ROOT = join(import.meta.dirname, '..');
const LAYER = join(SRC_ROOT, 'components', 'ui');

function sourceFiles(dir: string): string[] {
  const found: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      found.push(...sourceFiles(full));
    } else if (entry.name.endsWith('.tsx') && !entry.name.endsWith('.test.tsx')) {
      found.push(full);
    }
  }
  return found;
}

/** Views only. The control layer is where bare elements are allowed to live. */
const views = () => sourceFiles(SRC_ROOT).filter((file) => !file.startsWith(LAYER));

/**
 * Blank out comments, keeping newlines so line numbers survive. Without this the guard reports
 * every prose mention of `<select>` — the codebase explains its select behaviour at length.
 */
function withoutComments(source: string): string {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '))
    .replace(/\/\/[^\n]*/g, (m) => ' '.repeat(m.length));
}

/** Matches across lines: these elements are usually written one attribute per line. */
function hits(pattern: RegExp): string[] {
  const found: string[] = [];
  for (const file of views()) {
    const source = withoutComments(readFileSync(file, 'utf8'));
    for (const match of source.matchAll(pattern)) {
      const line = source.slice(0, match.index).split('\n').length;
      found.push(`${relative(SRC_ROOT, file)}:${line}`);
    }
  }
  return found.sort();
}

describe('control layer', () => {
  it('renders no control the layer does not style', () => {
    // Composing a native element with a layer class is fine — the clip dialog's radios carry a
    // title and a note, which the RadioOption component deliberately does not model. What is not
    // fine is an element the layer styles nothing about, which is how the player ended up with an
    // OS-chrome select and slider sitting beside styled controls.
    const styled = /className=(?:"[^"]*\b(?:input|select|slider|checkbox|radio)\b|\{[^}]*\b(?:input|select|slider|checkbox|radio)\b)/;
    const bare = [...hits(/<select\b[\s\S]{0,400}?>|<input\b[\s\S]{0,400}?type=["'](?:range|checkbox|radio)["'][\s\S]{0,400}?>/gs)];
    const offenders = bare.filter((location) => {
      const [file, line] = location.split(':');
      const source = withoutComments(readFileSync(join(SRC_ROOT, file), 'utf8')).split('\n');
      const element = source.slice(Number(line) - 1, Number(line) + 10).join('\n');
      return !styled.test(element);
    });
    expect(offenders).toEqual([]);
  });

  it('is not bypassed by reaching for .btn directly', () => {
    // `.btn` alone has no variant, so it renders with no background and a transparent border.
    expect(hits(/className=["'][^"']*\bbtn\b/g)).toEqual([]);
  });

  it('uses drawn icons rather than unicode glyphs', () => {
    expect(hits(/>[\s]*[◈⌁⚙▶⏸★☆🗑✕][\s]*</gu)).toEqual([]);
  });
});
