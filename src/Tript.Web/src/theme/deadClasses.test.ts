// SPDX-License-Identifier: GPL-2.0-or-later

import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { moduleFiles, sourceFiles, stylesheetPaths, styledClassesIn } from './cssRules';

const SRC_ROOT = join(import.meta.dirname, '..');

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
