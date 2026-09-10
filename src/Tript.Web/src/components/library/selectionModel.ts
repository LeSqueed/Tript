// SPDX-License-Identifier: GPL-2.0-or-later

import type { ContentItem } from '../../ipc/protocol';

export type SelectionKey = string;

export function selectionKey(item: ContentItem): SelectionKey {
  return `${item.contentType}:${item.filePath}`;
}

export function isSelected(selected: readonly SelectionKey[], key: SelectionKey): boolean {
  return selected.includes(key);
}

export function toggleSelection(
  selected: readonly SelectionKey[],
  key: SelectionKey,
): SelectionKey[] {
  return selected.includes(key) ? selected.filter((k) => k !== key) : [...selected, key];
}

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

export function pruneSelection(
  selected: readonly SelectionKey[],
  available: Iterable<SelectionKey>,
): SelectionKey[] {
  const keep = available instanceof Set ? available : new Set(available);
  return selected.filter((key) => keep.has(key));
}

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

export function selectedItems(
  items: readonly ContentItem[],
  selected: readonly SelectionKey[],
): ContentItem[] {
  const chosen = new Set(selected);
  return items.filter((item) => chosen.has(selectionKey(item)));
}

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
