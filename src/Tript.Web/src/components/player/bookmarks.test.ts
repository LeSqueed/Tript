// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { BookmarkItem } from '../../ipc/protocol';
import {
  bookmarkColor,
  bookmarkKindLabel,
  filterBookmarks,
  summarizeBookmarks,
} from './bookmarks';

const at = (id: string, type: string, time: number): BookmarkItem => ({ id, type, time });

describe('bookmarkColor', () => {
  it('maps the known vocabulary to stable colours', () => {
    expect(bookmarkColor('kill')).toBe('#f87171');
    expect(bookmarkColor('goal')).toBe('#4aa8ff');
  });

  it('gives manual bookmarks a colour of their own', () => {
    expect(bookmarkColor('manual')).toBe('#a78bfa');
    expect(bookmarkColor('manual')).not.toBe(bookmarkColor('something-new'));
  });

  it('falls back to the accent for unknown types', () => {
    expect(bookmarkColor('something-new')).toBe('#22d3ee');
  });
});

describe('bookmarkKindLabel', () => {
  it('capitalises the wire type', () => {
    expect(bookmarkKindLabel('kill')).toBe('Kill');
    expect(bookmarkKindLabel('manual')).toBe('Manual');
    expect(bookmarkKindLabel('something-new')).toBe('Something-new');
  });

  it('names an empty type rather than rendering nothing', () => {
    expect(bookmarkKindLabel('')).toBe('Bookmark');
  });
});

describe('summarizeBookmarks', () => {
  it('counts each type present and orders them by the known vocabulary', () => {
    const summary = summarizeBookmarks([
      at('a', 'manual', 5),
      at('b', 'kill', 10),
      at('c', 'death', 20),
      at('d', 'kill', 30),
    ]);

    expect(summary.map((kind) => kind.type)).toEqual(['kill', 'death', 'manual']);
    expect(summary.map((kind) => kind.count)).toEqual([2, 1, 1]);
    expect(summary[0].label).toBe('Kill');
    expect(summary[0].color).toBe(bookmarkColor('kill'));
  });

  it('puts unknown types last, sorted, and leaves absent types out', () => {
    const summary = summarizeBookmarks([
      at('a', 'zebra', 1),
      at('b', 'goal', 2),
      at('c', 'aardvark', 3),
    ]);

    expect(summary.map((kind) => kind.type)).toEqual(['goal', 'aardvark', 'zebra']);
  });

  it('is empty when there are no bookmarks', () => {
    expect(summarizeBookmarks([])).toEqual([]);
  });
});

describe('filterBookmarks', () => {
  const bookmarks = [at('a', 'kill', 1), at('b', 'death', 2), at('c', 'manual', 3)];

  it('drops every bookmark of a hidden type', () => {
    const kept = filterBookmarks(bookmarks, new Set(['death']));
    expect(kept.map((bookmark) => bookmark.id)).toEqual(['a', 'c']);
  });

  it('returns the same list when nothing is hidden', () => {
    expect(filterBookmarks(bookmarks, new Set())).toBe(bookmarks);
  });

  it('can hide everything', () => {
    expect(filterBookmarks(bookmarks, new Set(['kill', 'death', 'manual']))).toEqual([]);
  });
});
