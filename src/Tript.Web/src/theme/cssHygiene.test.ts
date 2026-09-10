// SPDX-License-Identifier: GPL-2.0-or-later

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
