// SPDX-License-Identifier: GPL-2.0-or-later
//
// Words the UI retired. Settings keeps its technical vocabulary deliberately — the people who open
// those pages are the ones who need a control to match the encoder documentation it comes from — so
// the boundary is enforced here rather than trusted to memory. The other side of it is pinned in
// SettingsView.test.tsx ("keeps the technical labels technical").

import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { sourceFiles, moduleFiles } from './cssRules';

const SRC_ROOT = join(import.meta.dirname, '..');
const SETTINGS = join(SRC_ROOT, 'settings');
// Training uses "region" as the documented normalized ONNX contract, not as a general clip term.
const TRAINING_REGION_SURFACES = new Set([
  join(SRC_ROOT, 'components', 'TrainingRegionEditor.tsx'),
  join(SRC_ROOT, 'components', 'TrainingSampleEditor.tsx'),
  join(SRC_ROOT, 'components', 'TrainingView.tsx'),
]);
/** The wire vocabulary. `Session` is a protocol value there, not a word anyone reads. */
const IPC = join(SRC_ROOT, 'ipc');
const RETIRED: { word: RegExp; instead: string }[] = [
  { word: /\brolling buffer\b/i, instead: 'instant replay' },
  { word: /\bmark in\b|\bmark out\b/i, instead: 'set start / set end' },
  { word: /\bstart capture\b/i, instead: 'record' },
  { word: /\bplayhead\b/i, instead: 'where you are' },
  { word: /\bsegments?\b/i, instead: 'clip(s)' },
  { word: /\bregions?\b/i, instead: 'clip(s)' },
  { word: /\b(in|out) point\b/i, instead: 'start / end' },
];

// Identifiers and wire names are out of scope on purpose. `RecordingMode.Session` is a real domain
// term the backend owns, and `session` runs through the IPC protocol; renaming those would change a
// contract to change a word nobody reads.

/**
 * `a > b && c < d` looks exactly like JSX text to the matcher above, and `region.start` reads as
 * prose containing a retired word. Rejecting these under-reports rather than accusing live code,
 * the same trade the CSS shadow analysis makes.
 */
function isCode(candidate: string): boolean {
  return /&&|\|\||=>|\w\.\w/.test(candidate);
}

/**
 * User-facing strings only: JSX text between tags, and the attributes that reach a person. Comments
 * are stripped first — the codebase explains the buffer at length and would otherwise report itself.
 */
function userFacingStrings(source: string): string[] {
  const code = source
    .replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '))
    .replace(/\/\/[^\n]*/g, (m) => ' '.repeat(m.length));
  const found: string[] = [];
  // Prose between tags. `=` and `;` are excluded so an arrow function's `=>` cannot open a match
  // that then runs through the code after it — the first version reported `region.id === …` as a
  // user-facing string.
  for (const match of code.matchAll(/>([^<>{}=;]{3,})</g)) {
    if (isCode(match[1])) continue;
    found.push(match[1]);
  }
  for (const match of code.matchAll(
    /(?:label|title|placeholder|aria-label|hint|confirmLabel|emptyLabel)="([^"]+)"/g,
  )) {
    found.push(match[1]);
  }
  // Display helpers live in plain modules too, where a label is a `return \'Session\'` rather than
  // JSX. Only prose-shaped literals — a leading capital — so protocol values like \'sessions\' and
  // css class names stay out of scope.
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
          if (word.source.includes('region') && TRAINING_REGION_SURFACES.has(file)) continue;
          if (word.test(candidate)) {
            offenders.push(`${relative(SRC_ROOT, file)}: "${candidate.trim()}" — say ${instead}`);
          }
        }
      }
    }
    expect(offenders).toEqual([]);
  });
});
