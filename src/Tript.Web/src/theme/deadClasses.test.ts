// SPDX-License-Identifier: GPL-2.0-or-later
//
// A class nothing renders is not harmless: it is a rule the next person reads as live, edits, and
// wonders why nothing moved. This guard found `.library-empty` — a whole panel treatment left behind
// when the empty states moved to the shared `EmptyState` component, still carrying a background and
// a border that nothing had worn for weeks — and `.app-rail`, which outlived the rail itself.

import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { moduleFiles, sourceFiles, stylesheetPaths, styledClassesIn } from './cssRules';

const SRC_ROOT = join(import.meta.dirname, '..');

/**
 * Prefixes whose full class names are composed at runtime — `btn-${variant}`, `status-dot-${kind}`
 * — so the literal never appears in the source and a plain search cannot see them. Keep this list
 * as short as the code allows: every entry is a place the guard is blind.
 */
const COMPOSED = ['btn-', 'status-dot-'];

describe('css classes', () => {
  it('are all worn by something', () => {
    const code = [...sourceFiles(SRC_ROOT), ...moduleFiles(SRC_ROOT)]
      .map((file) => readFileSync(file, 'utf8'))
      .join('\n');

    const dead: string[] = [];
    for (const sheet of stylesheetPaths(SRC_ROOT)) {
      for (const selector of styledClassesIn(readFileSync(sheet, 'utf8'))) {
        const name = selector.replace(/^\./, '');
        if (COMPOSED.some((prefix) => name.startsWith(prefix))) continue;
        if (!code.includes(name)) {
          dead.push(`${relative(SRC_ROOT, sheet)}: .${name}`);
        }
      }
    }

    expect(dead).toEqual([]);
  });
});
