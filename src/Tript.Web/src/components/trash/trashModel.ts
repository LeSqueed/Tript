// SPDX-License-Identifier: GPL-2.0-or-later

import type { TrashEntry } from '../../ipc/protocol';
import {
  contentTypeLabel,
  contentLabel,
  formatContentDuration,
  formatContentSize,
} from '../contentPresentation';
import {
  cleanGame,
  filterCatalogue,
  sortCatalogue,
  type CatalogueRecord,
} from '../catalogueModel';
import { type LibraryQuery } from '../library/libraryModel';

export const DEFAULT_RETENTION_HOURS = 24;

export interface TrashState {
  entries: TrashEntry[];
  retentionHours: number;
}

export const EMPTY_TRASH_STATE: TrashState = {
  entries: [],
  retentionHours: DEFAULT_RETENTION_HOURS,
};

const MINUTE = 60;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

function isTrashEntry(value: unknown): value is TrashEntry {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const record = value as Partial<TrashEntry>;
  return typeof record.id === 'string' && record.id.length > 0 && typeof record.fileName === 'string';
}

export function parseTrashMessage(content: unknown): TrashState | null {
  if (typeof content !== 'object' || content === null) {
    return null;
  }
  const record = content as { entries?: unknown; retentionHours?: unknown };
  if (!Array.isArray(record.entries)) {
    return null;
  }
  const retentionHours =
    typeof record.retentionHours === 'number' && Number.isFinite(record.retentionHours)
      ? record.retentionHours
      : DEFAULT_RETENTION_HOURS;
  return { entries: sortTrashEntries(record.entries.filter(isTrashEntry)), retentionHours };
}

export function sortTrashEntries(entries: readonly TrashEntry[]): TrashEntry[] {
  return [...entries].sort((a, b) => {
    const byTime = (b.deletedAt ?? 0) - (a.deletedAt ?? 0);
    return byTime !== 0 ? byTime : trashEntryLabel(a).localeCompare(trashEntryLabel(b));
  });
}

export function toTrashRecord(entry: TrashEntry): CatalogueRecord {
  return {
    contentType: entry.contentType,
    game: cleanGame(entry.game),
    title: entry.title ?? '',
    fileName: entry.fileName,
    date: typeof entry.deletedAt === 'number' && entry.deletedAt > 0 ? entry.deletedAt : undefined,
    label: trashEntryLabel(entry),
  };
}

export function filterTrashEntries(
  entries: readonly TrashEntry[],
  query: LibraryQuery,
  nowSeconds: number,
): TrashEntry[] {
  return sortCatalogue(
    filterCatalogue(entries, query, nowSeconds, toTrashRecord),
    query.sort,
    toTrashRecord,
  );
}

export function trashEntryLabel(entry: TrashEntry): string {
  return contentLabel(entry.title, entry.fileName);
}

export function trashTypeLabel(entry: TrashEntry): string {
  return contentTypeLabel(entry.contentType);
}

export function formatTrashSize(entry: TrashEntry): string | null {
  return formatContentSize(entry.fileSizeBytes);
}

export function formatTrashDuration(entry: TrashEntry): string | null {
  return formatContentDuration(entry.durationSeconds);
}

function plural(count: number, unit: string): string {
  return `${count} ${unit}${count === 1 ? '' : 's'}`;
}

export function formatElapsed(seconds: number): string {
  const total = Number.isFinite(seconds) ? Math.max(0, Math.round(seconds)) : 0;
  if (total < MINUTE) {
    return 'less than a minute';
  }
  if (total < HOUR) {
    return plural(Math.floor(total / MINUTE), 'minute');
  }
  if (total < DAY) {
    return plural(Math.floor(total / HOUR), 'hour');
  }
  return plural(Math.floor(total / DAY), 'day');
}

export function formatDeletedAt(entry: TrashEntry, nowSeconds: number): string {
  const deletedAt = entry.deletedAt;
  if (typeof deletedAt !== 'number' || !Number.isFinite(deletedAt) || deletedAt <= 0) {
    return 'Deleted at an unknown time';
  }
  return `Deleted ${formatElapsed(nowSeconds - deletedAt)} ago`;
}

export function formatPurgeAt(entry: TrashEntry, nowSeconds: number): string {
  const purgeAt = entry.purgeAt;
  if (typeof purgeAt !== 'number' || !Number.isFinite(purgeAt) || purgeAt <= 0) {
    return 'Kept until you empty the trash';
  }
  if (purgeAt <= nowSeconds) {
    return 'Due to be deleted';
  }
  return `Deleted for good in ${formatElapsed(purgeAt - nowSeconds)}`;
}

export function formatRetention(hours: number): string | null {
  if (typeof hours !== 'number' || !Number.isFinite(hours) || hours <= 0) {
    return null;
  }
  const whole = Math.max(1, Math.round(hours));
  if (whole >= 24 && whole % 24 === 0) {
    return plural(whole / 24, 'day');
  }
  return plural(whole, 'hour');
}

export function retentionNotice(retentionHours: number): string {
  const retention = formatRetention(retentionHours);
  return retention === null
    ? 'Items stay here until you delete them permanently or empty the trash.'
    : `Items are kept for ${retention}, then deleted for good.`;
}

export function deletionNotice(
  count: number,
  permanent: boolean,
  retentionHours: number,
): string {
  const items = `${count} item${count === 1 ? '' : 's'}`;
  if (permanent) {
    return `${items} will be deleted from disk immediately. This cannot be undone.`;
  }
  const retention = formatRetention(retentionHours);
  const they = count === 1 ? 'it can be restored' : 'they can be restored';
  return retention === null
    ? `${items} will be moved to the trash, where ${they} until you empty it.`
    : `${items} will be moved to the trash, where ${they} for the next ${retention}.`;
}
