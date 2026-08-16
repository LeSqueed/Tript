// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { bookmarkColor } from './bookmarks';

describe('bookmarkColor', () => {
  it('maps the known vocabulary to stable colours', () => {
    expect(bookmarkColor('kill')).toBe('#f87171');
    expect(bookmarkColor('goal')).toBe('#4aa8ff');
  });

  it('falls back to the accent for unknown types', () => {
    expect(bookmarkColor('something-new')).toBe('#22d3ee');
  });
});
