// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { ContentItem } from '../../ipc/protocol';
import {
  addSelection,
  allSelected,
  isSelected,
  pruneSelection,
  removeSelection,
  selectedItems,
  selectionKey,
  summarizeNames,
  toggleSelection,
} from './selectionModel';

function item(overrides: Partial<ContentItem> & { fileName: string }): ContentItem {
  return {
    contentType: 'recording',
    filePath: `sessions/${overrides.fileName}`,
    ...overrides,
  };
}

const a = item({ fileName: 'a.mp4', title: 'Ranked win' });
const b = item({ fileName: 'b.mp4', title: 'Overtime' });
const clip = item({ contentType: 'clip', fileName: 'a.mp4', filePath: 'clips/a.mp4', title: 'Nice shot' });

describe('selectionKey', () => {
  it('qualifies the path by content type, so a clip and a session cannot collide', () => {
    expect(selectionKey(a)).toBe('recording:sessions/a.mp4');
    expect(selectionKey(clip)).toBe('clip:clips/a.mp4');
    expect(selectionKey(a)).not.toBe(selectionKey(clip));
  });
});

describe('selection set helpers', () => {
  it('toggles a key in and out, keeping the order it was first selected in', () => {
    let selected = toggleSelection([], selectionKey(a));
    selected = toggleSelection(selected, selectionKey(b));
    expect(selected).toEqual([selectionKey(a), selectionKey(b)]);
    expect(isSelected(selected, selectionKey(a))).toBe(true);

    selected = toggleSelection(selected, selectionKey(a));
    expect(selected).toEqual([selectionKey(b)]);
    expect(isSelected(selected, selectionKey(a))).toBe(false);
  });

  it('adds without duplicating and removes without disturbing the rest', () => {
    const selected = addSelection([selectionKey(a)], [selectionKey(a), selectionKey(b)]);
    expect(selected).toEqual([selectionKey(a), selectionKey(b)]);

    expect(removeSelection(selected, [selectionKey(a)])).toEqual([selectionKey(b)]);
    expect(removeSelection(selected, ['nothing:here'])).toEqual(selected);
  });

  it('never mutates the array it was given — a pushed list is shared with the source', () => {
    const original = [selectionKey(a)];
    toggleSelection(original, selectionKey(b));
    addSelection(original, [selectionKey(b)]);
    removeSelection(original, [selectionKey(a)]);
    expect(original).toEqual([selectionKey(a)]);
  });

  it('reports all-selected only when the page is non-empty and fully covered', () => {
    const keys = [selectionKey(a), selectionKey(b)];
    expect(allSelected(keys, keys)).toBe(true);
    expect(allSelected(keys, [selectionKey(a)])).toBe(false);
    expect(allSelected([], [])).toBe(false);
  });
});

describe('pruneSelection', () => {
  it('drops keys whose items have left the list', () => {
    const selected = [selectionKey(a), selectionKey(b)];
    expect(pruneSelection(selected, new Set([selectionKey(a)]))).toEqual([selectionKey(a)]);
  });

  it('keeps everything that is still there, in order, and accepts any iterable', () => {
    const selected = [selectionKey(b), selectionKey(a)];
    expect(pruneSelection(selected, [a, b].map(selectionKey))).toEqual(selected);
  });
});

describe('selectedItems', () => {
  it('resolves keys back to the live items, in list order', () => {
    expect(selectedItems([a, b, clip], [selectionKey(clip), selectionKey(a)])).toEqual([a, clip]);
  });

  it('cannot resolve an item that has gone, so a stale key sends nothing to the backend', () => {
    expect(selectedItems([a], [selectionKey(b)])).toEqual([]);
  });
});

describe('summarizeNames', () => {
  it('lists short sets in full', () => {
    expect(summarizeNames([])).toBe('');
    expect(summarizeNames(['Ranked win'])).toBe('Ranked win');
    expect(summarizeNames(['Ranked win', 'Overtime'])).toBe('Ranked win and Overtime');
    expect(summarizeNames(['a', 'b', 'c'])).toBe('a, b and c');
  });

  it('counts the rest once naming them stops helping', () => {
    expect(summarizeNames(['a', 'b', 'c', 'd'])).toBe('a, b, c and 1 more');
    expect(summarizeNames(['a', 'b', 'c', 'd', 'e'], 2)).toBe('a, b and 3 more');
  });

  it('ignores blank names rather than rendering a gap in the sentence', () => {
    expect(summarizeNames(['a', '   ', 'b'])).toBe('a and b');
  });
});
