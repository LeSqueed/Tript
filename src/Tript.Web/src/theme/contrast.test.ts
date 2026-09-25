// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';

function hexToRgb(hex: string): [number, number, number] {
  const clean = hex.replace('#', '');
  if (clean.length !== 6) {
    throw new Error(`not a #rrggbb colour: ${hex}`);
  }
  return [
    parseInt(clean.slice(0, 2), 16),
    parseInt(clean.slice(2, 4), 16),
    parseInt(clean.slice(4, 6), 16),
  ];
}

function luminance(hex: string): number {
  const [r, g, b] = hexToRgb(hex).map((c) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function contrastRatio(a: string, b: string): number {
  const la = luminance(a);
  const lb = luminance(b);
  const [hi, lo] = la >= lb ? [la, lb] : [lb, la];
  return (hi + 0.05) / (lo + 0.05);
}

const ACCENT = '#f08a72';
const ACCENT_CONTENT = '#25110d';
const PRIMARY = '#1b1d24';
const BASE_CONTENT = '#eee9e4';
const BASE_100 = '#101116';

describe('theme accent constraint', () => {
  it('dark text on the accent is legible (WCAG AA for normal text)', () => {
    expect(contrastRatio(ACCENT_CONTENT, ACCENT)).toBeGreaterThanOrEqual(7);
  });

  it('light text on the dark primary is legible', () => {
    expect(contrastRatio(PRIMARY, BASE_CONTENT)).toBeGreaterThanOrEqual(4.5);
  });

  it('the accent is light, not dark: the trap only bites dark accents', () => {
    const accentLum = luminance(ACCENT);
    expect(accentLum).toBeGreaterThan(0.35);
  });

  it('page text contrasts against the page ground', () => {
    expect(contrastRatio(BASE_CONTENT, BASE_100)).toBeGreaterThanOrEqual(7);
  });
});
