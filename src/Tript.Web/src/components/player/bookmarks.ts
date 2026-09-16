// SPDX-License-Identifier: GPL-2.0-or-later

import type { BookmarkItem } from '../../ipc/protocol';

const BOOKMARK_COLORS: Record<string, string> = {
  kill: '#f87171',
  death: '#f0b429',
  assist: '#36d399',
  goal: '#4aa8ff',
  manual: '#a78bfa',
};

const UNKNOWN_COLOR = '#22d3ee';

export const BOOKMARK_KINDS = ['kill', 'death', 'assist', 'goal', 'manual'] as const;

export interface BookmarkKindSummary {
  type: string;
  label: string;
  color: string;
  count: number;
}

export function bookmarkColor(type: string): string {
  return BOOKMARK_COLORS[type] ?? UNKNOWN_COLOR;
}

export function bookmarkKindLabel(type: string): string {
  if (type.length === 0) {
    return 'Bookmark';
  }
  return type.charAt(0).toUpperCase() + type.slice(1);
}

export function summarizeBookmarks(bookmarks: BookmarkItem[]): BookmarkKindSummary[] {
  const counts = new Map<string, number>();
  for (const bookmark of bookmarks) {
    counts.set(bookmark.type, (counts.get(bookmark.type) ?? 0) + 1);
  }

  const known = BOOKMARK_KINDS.filter((kind) => counts.has(kind));
  const unknown = [...counts.keys()]
    .filter((type) => !(BOOKMARK_KINDS as readonly string[]).includes(type))
    .sort();

  return [...known, ...unknown].map((type) => ({
    type,
    label: bookmarkKindLabel(type),
    color: bookmarkColor(type),
    count: counts.get(type) ?? 0,
  }));
}

export function filterBookmarks(
  bookmarks: BookmarkItem[],
  hiddenKinds: ReadonlySet<string>,
): BookmarkItem[] {
  if (hiddenKinds.size === 0) {
    return bookmarks;
  }
  return bookmarks.filter((bookmark) => !hiddenKinds.has(bookmark.type));
}
