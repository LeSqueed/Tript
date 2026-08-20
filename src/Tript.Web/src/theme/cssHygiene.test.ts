// SPDX-License-Identifier: GPL-2.0-or-later
//
// The previous redesign appended "replacement" blocks to the bottom of five stylesheets instead of
// editing the rules they superseded, leaving dead declarations behind that still cost a reader time.
// One declaration per selector per scope, enforced.
//
// Scope, not file: a selector may legitimately appear once at the top level and again inside a
// media query. Two declarations inside the *same* block are the smell this guards against.
//
// KNOWN_DUPLICATES is a ratchet — exact, not a ceiling. Fixing a file fails this test until the
// entry is updated, which is the point: the debt cannot quietly stop shrinking.
//
// Known limitation: the key is the whole selector text, so `.a, .b {}` and a later `.b {}` are two
// different keys and are not reported. That is deliberate — a shared rule plus a specific override
// is ordinary CSS, and flagging it would bury the signal this guard exists for.

import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { parseRules, stylesheetPaths } from './cssRules';

const SRC_ROOT = join(import.meta.dirname, '..');

const KNOWN_DUPLICATES: Record<string, number> = {
  'components/LibraryView.css': 10,
  'components/PlayerView.css': 6,
  'components/TrashView.css': 2,
};

/** Selectors declared more than once in the same scope, as `selector (Nx) @ line,line`. */
export function duplicatesIn(css: string): string[] {
  const scopes = new Map<string, { lines: number[] }>();
  for (const rule of parseRules(css)) {
    const key = `${rule.context.join(' > ')}||${rule.selector}`;
    const entry = scopes.get(key) ?? { lines: [] };
    entry.lines.push(rule.line);
    scopes.set(key, entry);
  }
  return [...scopes.entries()]
    .filter(([, entry]) => entry.lines.length > 1)
    .map(([key, entry]) => `${key.split('||')[1]} (${entry.lines.length}x) @ ${entry.lines.join(',')}`)
    .sort();
}

describe('css hygiene', () => {
  it('declares each selector at most once per scope', () => {
    const actual: Record<string, number> = {};
    const detail: string[] = [];
    for (const file of stylesheetPaths(SRC_ROOT)) {
      const name = relative(SRC_ROOT, file);
      const duplicated = duplicatesIn(readFileSync(file, 'utf8'));
      if (duplicated.length > 0) {
        actual[name] = duplicated.length;
        detail.push(`${name}: ${duplicated.join('; ')}`);
      }
    }
    expect(actual, detail.join('\n')).toEqual(KNOWN_DUPLICATES);
  });
});
