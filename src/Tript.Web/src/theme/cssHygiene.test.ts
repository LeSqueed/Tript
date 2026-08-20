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

import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  duplicatesIn,
  repeatedPropertiesIn,
  shadowedRules,
  styledClassesIn,
  stylesheetPaths,
} from './cssRules';

const SRC_ROOT = join(import.meta.dirname, '..');

const KNOWN_DUPLICATES: Record<string, number> = {};

/**
 * Rules that render nothing because a later rule overrides every declaration. A separate ratchet
 * because duplicatesIn structurally cannot see them: `.a, .b {}` and a later `.a {}` are different
 * selector keys. TrashView.css had one — an appended block sitting above what it meant to replace,
 * so the redesign's surface never rendered at all and no test noticed.
 */
const KNOWN_SHADOWED: Record<string, number> = {};

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

  it('gives each class exactly one stylesheet', () => {
    // `.btn` used to have its base in LibraryView.css and variants across three more files, with
    // :disabled declared twice with different values — whichever loaded last won. A view may still
    // position a shared control (`.settings-row .input`); that is about .settings-row, not .input.
    const homes = new Map<string, string[]>();
    for (const file of stylesheetPaths(SRC_ROOT)) {
      for (const styled of styledClassesIn(readFileSync(file, 'utf8'))) {
        homes.set(styled, [...(homes.get(styled) ?? []), relative(SRC_ROOT, file)]);
      }
    }
    const scattered = [...homes.entries()]
      .filter(([, files]) => files.length > 1)
      .map(([styled, files]) => `${styled} in ${files.join(', ')}`)
      .sort();
    expect(scattered).toEqual([]);
  });

  it('declares each property at most once per rule', () => {
    const offenders: string[] = [];
    for (const file of stylesheetPaths(SRC_ROOT)) {
      for (const repeat of repeatedPropertiesIn(readFileSync(file, 'utf8'))) {
        offenders.push(`${relative(SRC_ROOT, file)}: ${repeat}`);
      }
    }
    expect(offenders).toEqual([]);
  });

  it('carries no rule whose every declaration a later rule overrides', () => {
    const actual: Record<string, number> = {};
    const detail: string[] = [];
    for (const file of stylesheetPaths(SRC_ROOT)) {
      const name = relative(SRC_ROOT, file);
      const dead = shadowedRules(readFileSync(file, 'utf8'));
      if (dead.length > 0) {
        actual[name] = dead.length;
        detail.push(`${name}: ${dead.join('; ')}`);
      }
    }
    expect(actual, detail.join('\n')).toEqual(KNOWN_SHADOWED);
  });
});
