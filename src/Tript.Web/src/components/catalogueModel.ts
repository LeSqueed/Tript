// SPDX-License-Identifier: GPL-2.0-or-later
//
// The catalogue's filter and sort dimensions, shared by the library and the trash. Both list the
// same content in two states — live and deleted — so the rules for "does this match the type /
// game / date / search filters" and "in what order" live here once, not per view.
//
// Everything operates on a `CatalogueRecord`, the small projection each view builds from its own
// wire type (`ContentItem` in the library, `TrashEntry` in the trash). Keeping the shared logic on
// that projection rather than on the raw types is what lets a TrashEntry and a ContentItem answer
// the same questions without either knowing about the other.

import type { ContentType } from '../ipc/protocol';

export type ContentTypeFilter = 'all' | 'sessions' | 'clips' | 'highlights' | 'trash';

export type DateRangeFilter = 'any' | 'day' | 'week' | 'month' | 'year';

export type LibrarySort = 'newest' | 'oldest' | 'game';

export const ANY_GAME = '__any_game__';
export const NO_GAME = '__no_game__';

export const DATE_RANGE_SECONDS: Record<DateRangeFilter, number | null> = {
  any: null,
  day: 24 * 60 * 60,
  week: 7 * 24 * 60 * 60,
  month: 30 * 24 * 60 * 60,
  year: 365 * 24 * 60 * 60,
};

/** The shared projection a filter or sort reads. Each view builds one from its own wire type. */
export interface CatalogueRecord {
  contentType: ContentType;
  /** The cleaned game name, or null when the record has none — see `cleanGame`. */
  game: string | null;
  title: string;
  fileName: string;
  /** Epoch seconds, or undefined when the record carries no usable timestamp. */
  date?: number;
  /** The display name, used as the last tiebreak in a sort. */
  label: string;
}

export interface CatalogueQuery {
  type: ContentTypeFilter;
  game: string;
  range: DateRangeFilter;
  search: string;
  sort: LibrarySort;
}

/** A raw game field (absent, null, or blank) is "no game" — the same three shapes both views see. */
export function cleanGame(raw: string | null | undefined): string | null {
  if (typeof raw !== 'string') {
    return null;
  }
  const trimmed = raw.trim();
  return trimmed.length > 0 ? trimmed : null;
}

export function typeMatches(record: CatalogueRecord, filter: ContentTypeFilter): boolean {
  // `trash` is not a real content type — a record filtered as "trash" is a nonsense combination, so
  // it matches everything rather than silently reading as "sessions".
  if (filter === 'all' || filter === 'trash') {
    return true;
  }
  if (filter === 'clips') {
    return record.contentType === 'clip';
  }
  if (filter === 'highlights') {
    return record.contentType === 'highlight';
  }
  return record.contentType !== 'clip' && record.contentType !== 'highlight';
}

export function gameMatches(record: CatalogueRecord, game: string): boolean {
  if (game === ANY_GAME) {
    return true;
  }
  if (game === NO_GAME) {
    return record.game === null;
  }
  return record.game !== null && record.game.toLowerCase() === game.toLowerCase();
}

/**
 * Is the record inside the trailing window ending now? A record with no date does NOT match a
 * window: a window is a claim about when something happened, and an undated record cannot support it.
 */
export function dateMatches(record: CatalogueRecord, range: DateRangeFilter, nowSeconds: number): boolean {
  const window = DATE_RANGE_SECONDS[range];
  if (window === null) {
    return true;
  }
  const date = record.date;
  if (date === undefined) {
    return false;
  }
  return date >= nowSeconds - window;
}

/**
 * A case-insensitive substring of the title, the file name or the game. The file name is searched as
 * well as the title because a record with no metadata has only a file name — searching just titles
 * would make exactly the records with the least metadata the hardest to find.
 */
export function searchMatches(record: CatalogueRecord, search: string): boolean {
  const needle = search.trim().toLowerCase();
  if (needle.length === 0) {
    return true;
  }
  const haystack = [record.title, record.fileName, record.game]
    .filter((part): part is string => typeof part === 'string')
    .join(' ')
    .toLowerCase();
  return haystack.includes(needle);
}

/** Every dimension at once, in the order that rejects cheapest-first. */
export function filterCatalogue<T>(
  items: readonly T[],
  query: CatalogueQuery,
  nowSeconds: number,
  toRecord: (item: T) => CatalogueRecord,
  /** An extra keep-rule a view applies that the shared dimensions cannot express. */
  guard?: (item: T) => boolean,
): T[] {
  return items.filter((item) => {
    const record = toRecord(item);
    return (
      typeMatches(record, query.type) &&
      gameMatches(record, query.game) &&
      dateMatches(record, query.range, nowSeconds) &&
      searchMatches(record, query.search) &&
      (guard ? guard(item) : true)
    );
  });
}

/**
 * The sort order the sort control offers. Undated and unknown-game records go last — "unknown" is
 * not "infinitely old" or "A", so floating them to the top would bury the real oldest / first items.
 */
export function compareRecords(a: CatalogueRecord, b: CatalogueRecord, sort: LibrarySort): number {
  const byLabel = (x: CatalogueRecord, y: CatalogueRecord) =>
    x.label.localeCompare(y.label, undefined, { sensitivity: 'base' });

  const byDate = (x: CatalogueRecord, y: CatalogueRecord, direction: 1 | -1) => {
    if (x.date === undefined && y.date === undefined) {
      return byLabel(x, y);
    }
    if (x.date === undefined) {
      return 1;
    }
    if (y.date === undefined) {
      return -1;
    }
    return x.date === y.date ? byLabel(x, y) : (x.date - y.date) * direction;
  };

  if (sort === 'oldest') {
    return byDate(a, b, 1);
  }
  if (sort === 'game') {
    if (a.game !== b.game) {
      if (a.game === null) {
        return 1;
      }
      if (b.game === null) {
        return -1;
      }
      const byGame = a.game.localeCompare(b.game, undefined, { sensitivity: 'base' });
      if (byGame !== 0) {
        return byGame;
      }
    }
    return byDate(a, b, -1);
  }
  return byDate(a, b, -1);
}

/** Reorder the list. Never mutates the input (a push's array is shared with the source). */
export function sortCatalogue<T>(
  items: readonly T[],
  sort: LibrarySort,
  toRecord: (item: T) => CatalogueRecord,
): T[] {
  return [...items].sort((a, b) => compareRecords(toRecord(a), toRecord(b), sort));
}
