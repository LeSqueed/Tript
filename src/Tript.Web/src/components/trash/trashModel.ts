// SPDX-License-Identifier: GPL-2.0-or-later
//
// The trash's derivation model — parsing the `trash` push and formatting an entry, as pure
// functions, in the same spirit as library/libraryModel.ts.
//
// Two things here are worth more than they look:
//
//   - retention is the backend's number, not ours. The delete confirmation says how long an item
//     survives in the trash, and saying "24 hours" when the host is configured for 72 (or for never)
//     turns a safety net into a lie. Every sentence about retention is built from `retentionHours`
//     off the wire, and `<= 0` means "never auto-purge" rather than "gone immediately".
//   - the times on the wire are epoch SECONDS. Multiplying by 1000 in one place, here, is why no
//     component ever has to remember that.

import type { TrashEntry } from '../../ipc/protocol';
import {
  DATE_RANGE_SECONDS,
  formatBytes,
  type LibraryQuery,
} from '../library/libraryModel';
import { formatTime } from '../player/timelineModel';

/** What the backend defaults to, used until the first `trash` push says otherwise. */
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

// ---------------------------------------------------------------------------
// Parsing the push
// ---------------------------------------------------------------------------

function isTrashEntry(value: unknown): value is TrashEntry {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const record = value as Partial<TrashEntry>;
  // An entry with no id cannot be restored or purged, so it is worse than useless in the list.
  return typeof record.id === 'string' && record.id.length > 0 && typeof record.fileName === 'string';
}

/** Parse a `trash` message. Returns null for a frame that is not one — never a half-built state. */
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

/** Most recently deleted first — the order a trash list is read in. */
export function sortTrashEntries(entries: readonly TrashEntry[]): TrashEntry[] {
  return [...entries].sort((a, b) => {
    const byTime = (b.deletedAt ?? 0) - (a.deletedAt ?? 0);
    return byTime !== 0 ? byTime : trashEntryLabel(a).localeCompare(trashEntryLabel(b));
  });
}

/** Apply the Library's useful catalogue dimensions to deleted entries too. */
export function filterTrashEntries(
  entries: readonly TrashEntry[],
  query: LibraryQuery,
  nowSeconds: number,
): TrashEntry[] {
  const window = DATE_RANGE_SECONDS[query.range];
  const search = query.search.trim().toLowerCase();
  return [...entries]
    .filter((entry) => {
      const isClip = entry.contentType === 'clip' || entry.contentType === 'highlight';
      if (query.type === 'clips' && !isClip) return false;
      if (query.type === 'sessions' && isClip) return false;
      if (query.game !== '__any_game__') {
        const game = typeof entry.game === 'string' && entry.game.trim().length > 0 ? entry.game.trim() : null;
        if (query.game === '__no_game__' ? game !== null : game?.toLowerCase() !== query.game.toLowerCase()) {
          return false;
        }
      }
      if (window !== null && (entry.deletedAt <= 0 || entry.deletedAt < nowSeconds - window)) return false;
      if (search.length > 0 && ![entry.title, entry.fileName, entry.game].filter(Boolean).join(' ').toLowerCase().includes(search)) {
        return false;
      }
      return true;
    })
    .sort((a, b) => {
      if (query.sort === 'oldest') return (a.deletedAt ?? 0) - (b.deletedAt ?? 0);
      if (query.sort === 'game') return (a.game ?? '').localeCompare(b.game ?? '') || (b.deletedAt ?? 0) - (a.deletedAt ?? 0);
      return (b.deletedAt ?? 0) - (a.deletedAt ?? 0);
    });
}

// ---------------------------------------------------------------------------
// Reading an entry
// ---------------------------------------------------------------------------

/** The entry's display name: its title, or the file name it was saved under. */
export function trashEntryLabel(entry: TrashEntry): string {
  const title = entry.title?.trim();
  return title && title.length > 0 ? title : entry.fileName;
}

/** The human label for what the entry was. Mirrors the library's type chip. */
export function trashTypeLabel(entry: TrashEntry): string {
  switch (entry.contentType) {
    case 'clip':
      return 'Clip';
    case 'highlight':
      return 'Highlight';
    case 'buffer':
      return 'Buffer';
    default:
      return 'Recording';
  }
}

export function formatTrashSize(entry: TrashEntry): string | null {
  return formatBytes(entry.fileSizeBytes);
}

export function formatTrashDuration(entry: TrashEntry): string | null {
  const seconds = entry.durationSeconds;
  return typeof seconds === 'number' && Number.isFinite(seconds) && seconds > 0
    ? formatTime(seconds)
    : null;
}

// ---------------------------------------------------------------------------
// Times
// ---------------------------------------------------------------------------

function plural(count: number, unit: string): string {
  return `${count} ${unit}${count === 1 ? '' : 's'}`;
}

/** A span of seconds as a coarse magnitude — "3 minutes", "22 hours", "2 days". */
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

/** When the entry was deleted, relative to now. `nowSeconds` is injected so tests are deterministic. */
export function formatDeletedAt(entry: TrashEntry, nowSeconds: number): string {
  const deletedAt = entry.deletedAt;
  if (typeof deletedAt !== 'number' || !Number.isFinite(deletedAt) || deletedAt <= 0) {
    return 'Deleted at an unknown time';
  }
  return `Deleted ${formatElapsed(nowSeconds - deletedAt)} ago`;
}

/**
 * When the entry will be purged. Retention disabled (`purgeAt === 0`) is stated as a promise rather
 * than left blank: "nothing here says when this goes" reads as missing data, not as "it never does".
 */
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

/** The retention window as a phrase, or null when the backend never auto-purges. */
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

/** The standing note above the trash list, so the retention rule is visible before anything goes wrong. */
export function retentionNotice(retentionHours: number): string {
  const retention = formatRetention(retentionHours);
  return retention === null
    ? 'Items stay here until you delete them permanently or empty the trash.'
    : `Items are kept for ${retention}, then deleted for good.`;
}

// ---------------------------------------------------------------------------
// The confirmation's sentence
// ---------------------------------------------------------------------------

/**
 * Exactly what the confirm button is about to do, in one sentence: how many items, where they go,
 * and how long they can still be recovered for.
 */
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
