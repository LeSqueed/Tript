// SPDX-License-Identifier: GPL-2.0-or-later

import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { sourceFiles, moduleFiles } from './cssRules';

const SRC_ROOT = join(import.meta.dirname, '..');
const SETTINGS = join(SRC_ROOT, 'settings');
const TRAINING_REGION_SURFACES = new Set([
  join(SRC_ROOT, 'components', 'TrainingRegionEditor.tsx'),
  join(SRC_ROOT, 'components', 'TrainingEventTree.tsx'),
  join(SRC_ROOT, 'components', 'TrainingSampleEditor.tsx'),
  join(SRC_ROOT, 'components', 'TrainingView.tsx'),
]);
const TRAINING = join(SRC_ROOT, 'components', 'training');
const IPC = join(SRC_ROOT, 'ipc');
const EM_DASH = '—';
const RETIRED: { word: RegExp; instead: string }[] = [
  { word: /\brolling buffer\b/i, instead: 'instant replay' },
  { word: /\bmark in\b|\bmark out\b/i, instead: 'set start / set end' },
  { word: /\bstart capture\b/i, instead: 'record' },
  { word: /\bplayhead\b/i, instead: 'where you are' },
  { word: /\bsegments?\b/i, instead: 'clip(s)' },
  { word: /\bregions?\b/i, instead: 'clip(s)' },
  { word: /\b(in|out) point\b/i, instead: 'start / end' },
];

function isCode(candidate: string): boolean {
  return /&&|\|\||=>|\w\.\w/.test(candidate);
}

function withoutComments(source: string): string {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '))
    .replace(/\/\/[^\n]*/g, (m) => ' '.repeat(m.length));
}

function userFacingStrings(source: string): string[] {
  const code = withoutComments(source);
  const found: string[] = [];
  for (const match of code.matchAll(/>([^<>{}=;]{3,})</g)) {
    if (isCode(match[1])) continue;
    found.push(match[1]);
  }
  for (const match of code.matchAll(
    /(?:label|title|placeholder|aria-label|hint|confirmLabel|emptyLabel)="([^"]+)"/g,
  )) {
    found.push(match[1]);
  }
  for (const match of code.matchAll(/\'([A-Z][^\'\\\\]{2,})\'/g)) {
    found.push(match[1]);
  }
  return found;
}

describe('plain language outside settings', () => {
  it('uses the retired words nowhere but settings', () => {
    const offenders: string[] = [];
    const scanned = [...sourceFiles(SRC_ROOT), ...moduleFiles(SRC_ROOT)].filter(
      (file) => !file.startsWith(SETTINGS) && !file.startsWith(IPC),
    );
    for (const file of scanned) {
      for (const candidate of userFacingStrings(readFileSync(file, 'utf8'))) {
        for (const { word, instead } of RETIRED) {
          const trainingSurface = TRAINING_REGION_SURFACES.has(file) || file.startsWith(TRAINING);
          if (word.source.includes('region') && trainingSurface) continue;
          if (word.test(candidate)) {
            offenders.push(`${relative(SRC_ROOT, file)}: "${candidate.trim()}" — say ${instead}`);
          }
        }
      }
    }
    expect(offenders).toEqual([]);
  });

  it('never shows an em-dash to the user', () => {
    const offenders = new Set<string>();
    for (const file of [...sourceFiles(SRC_ROOT), ...moduleFiles(SRC_ROOT)]) {
      const source = readFileSync(file, 'utf8');
      const literals = [...withoutComments(source).matchAll(/[`'"]([^`'"\n]*)[`'"]/g)].map((match) => match[1]);
      for (const candidate of [...userFacingStrings(source), ...literals]) {
        if (candidate.includes(EM_DASH)) offenders.add(`${relative(SRC_ROOT, file)}: "${candidate.trim()}"`);
      }
    }
    expect([...offenders]).toEqual([]);
  });
});
