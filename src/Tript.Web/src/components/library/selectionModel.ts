// SPDX-License-Identifier: GPL-2.0-or-later
//
// Multi-select as a pure key set, kept out of the grid for the same reason the derivation model is
// (libraryModel.ts): what actually goes wrong with a selection is arithmetic over a list that moves
// under it. A `content` push replaces the whole item array, so the selection cannot be a set of
// item references — it is a set of stable keys, pruned against whatever the list currently holds.

import type { ContentItem } from '../../ipc/protocol';

export type SelectionKey = string;

/**
 * The key a content item is selected by. Type-qualified path: a clip and the recording it came from
 * can share neither, but the pair is unique and survives a rename (which keeps the path).
 */
export function selectionKey(item: ContentItem): SelectionKey {
  return `${item.contentType}:${item.filePath}`;
}

export function isSelected(selected: readonly SelectionKey[], key: SelectionKey): boolean {
  return selected.includes(key);
}

/** Add the key if absent, drop it if present. Order of first selection is preserved. */
export function toggleSelection(
  selected: readonly SelectionKey[],
  key: SelectionKey,
): SelectionKey[] {
  return selected.includes(key) ? selected.filter((k) => k !== key) : [...selected, key];
}

/** Add every key not already selected, appended in the order given. */
export function addSelection(
  selected: readonly SelectionKey[],
  keys: readonly SelectionKey[],
): SelectionKey[] {
  const next = [...selected];
  for (const key of keys) {
    if (!next.includes(key)) {
      next.push(key);
    }
  }
  return next;
}

export function removeSelection(
  selected: readonly SelectionKey[],
  keys: readonly SelectionKey[],
): SelectionKey[] {
  const drop = new Set(keys);
  return selected.filter((key) => !drop.has(key));
}

/** Drop every key that is not in `available` — the guard against a stale selection. */
export function pruneSelection(
  selected: readonly SelectionKey[],
  available: Iterable<SelectionKey>,
): SelectionKey[] {
  const keep = available instanceof Set ? available : new Set(available);
  return selected.filter((key) => keep.has(key));
}

/** Whether every one of `keys` is selected. An empty page is NOT "all selected". */
export function allSelected(
  keys: readonly SelectionKey[],
  selected: readonly SelectionKey[],
): boolean {
  if (keys.length === 0) {
    return false;
  }
  const chosen = new Set(selected);
  return keys.every((key) => chosen.has(key));
}

/**
 * The items a bulk action covers, in list order. Derived from the live list rather than remembered
 * alongside the keys, so an item that has since disappeared cannot be sent to the backend.
 */
export function selectedItems(
  items: readonly ContentItem[],
  selected: readonly SelectionKey[],
): ContentItem[] {
  const chosen = new Set(selected);
  return items.filter((item) => chosen.has(selectionKey(item)));
}

/**
 * Name a list of things for a confirmation prompt: every name up to `max`, then a count of the rest.
 * A destructive prompt has to say what it is about to destroy, and a wall of forty file names says
 * that no better than "and 37 more" does.
 */
export function summarizeNames(names: readonly string[], max = 3): string {
  const list = names.filter((name) => name.trim().length > 0);
  if (list.length === 0) {
    return '';
  }
  if (list.length === 1) {
    return list[0];
  }
  if (list.length <= max) {
    return `${list.slice(0, -1).join(', ')} and ${list[list.length - 1]}`;
  }
  const rest = list.length - max;
  return `${list.slice(0, max).join(', ')} and ${rest} more`;
}
