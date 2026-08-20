// SPDX-License-Identifier: GPL-2.0-or-later
//
// Typography invariants, verified mechanically. The redesign replaced a Georgia headline and a set
// of wide-tracked micro-caps "eyebrow" labels — the two signals that made the app read as a
// brochure — and this is what stops either coming back. Companion to contrast.test.ts.
//
// KNOWN_* below is a ratchet, not a permission slip: it lists the stylesheets still awaiting a
// cleanup phase, and it must shrink to empty. Anything not listed fails immediately, so the suite
// stays green commit by commit while the remaining debt stays visible.

import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { stylesheetPaths } from './cssRules';

const SRC_ROOT = join(import.meta.dirname, '..');

/** Stylesheets still declaring a serif family. Empty this; do not add to it. */
const KNOWN_SERIF = ['components/PlayerView.css'];

/** Stylesheets still shipping the tracked micro-caps eyebrow. Empty this; do not add to it. */
const KNOWN_TRACKED: string[] = [];

const stripComments = (css: string) => css.replace(/\/\*[\s\S]*?\*\//g, (match) => match.replace(/[^\n]/g, ' '));

function locate(file: string, css: string, pattern: RegExp): string[] {
  const hits: string[] = [];
  stripComments(css)
    .split('\n')
    .forEach((text, offset) => {
      if (pattern.test(text)) {
        hits.push(`${relative(SRC_ROOT, file)}:${offset + 1}`);
      }
    });
  return hits;
}

/** Any serif face, wherever it is declared — font-family, the `font` shorthand, or a custom property. */
const SERIF = /(Georgia|Times New Roman|(?<!sans-)\bserif\b)/i;

/** Tracked micro-caps: uppercase plus deliberate letter-spacing, in em or px, any casing. */
function isTrackedMicroCaps(block: string): boolean {
  if (!/text-transform\s*:\s*uppercase/i.test(block)) {
    return false;
  }
  const em = block.match(/letter-spacing\s*:\s*(0?\.\d+)\s*em/i);
  if (em && Number(em[1]) >= 0.08) {
    return true;
  }
  const px = block.match(/letter-spacing\s*:\s*(\d+(?:\.\d+)?)\s*px/i);
  return px !== null && Number(px[1]) >= 1;
}

describe('typography invariants', () => {
  it('declares a serif family only where a cleanup phase still owes one', () => {
    const offenders = new Set<string>();
    for (const file of stylesheetPaths(SRC_ROOT)) {
      for (const hit of locate(file, readFileSync(file, 'utf8'), SERIF)) {
        offenders.add(hit);
      }
    }
    const files = [...new Set([...offenders].map((hit) => hit.split(':')[0]))].sort();
    expect(files, `serif hits: ${[...offenders].sort().join(', ')}`).toEqual([...KNOWN_SERIF].sort());
  });

  it('ships the tracked micro-caps eyebrow only where a cleanup phase still owes one', () => {
    const offenders: string[] = [];
    for (const file of stylesheetPaths(SRC_ROOT)) {
      const css = stripComments(readFileSync(file, 'utf8'));
      let consumed = 0;
      for (const block of css.split('}')) {
        const line = css.slice(0, consumed).split('\n').length;
        consumed += block.length + 1;
        if (isTrackedMicroCaps(block)) {
          offenders.push(`${relative(SRC_ROOT, file)}:${line}`);
        }
      }
    }
    const files = [...new Set(offenders.map((hit) => hit.split(':')[0]))].sort();
    expect(files, `micro-caps hits: ${offenders.sort().join(', ')}`).toEqual([...KNOWN_TRACKED].sort());
  });
});
