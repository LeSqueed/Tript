// SPDX-License-Identifier: GPL-2.0-or-later

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

export interface CatalogueRecord {
  contentType: ContentType;
  game: string | null;
  title: string;
  fileName: string;
  date?: number;
  label: string;
}

export interface CatalogueQuery {
  type: ContentTypeFilter;
  game: string;
  range: DateRangeFilter;
  search: string;
  sort: LibrarySort;
}

export function cleanGame(raw: string | null | undefined): string | null {
  if (typeof raw !== 'string') {
    return null;
  }
  const trimmed = raw.trim();
  return trimmed.length > 0 ? trimmed : null;
}

export function typeMatches(record: CatalogueRecord, filter: ContentTypeFilter): boolean {
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

export function filterCatalogue<T>(
  items: readonly T[],
  query: CatalogueQuery,
  nowSeconds: number,
  toRecord: (item: T) => CatalogueRecord,
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

export function sortCatalogue<T>(
  items: readonly T[],
  sort: LibrarySort,
  toRecord: (item: T) => CatalogueRecord,
): T[] {
  return [...items].sort((a, b) => compareRecords(toRecord(a), toRecord(b), sort));
}
