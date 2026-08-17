// SPDX-License-Identifier: GPL-2.0-or-later
//
// The library's derivation model — filter → sort → paginate, as pure functions.
//
// The library is one grid over the *whole* content list (sessions and clips together); there is no
// separate clips page, because "is it a clip" is a filter dimension exactly like the game and the
// date are. Everything the grid shows is derived here rather than in the component, for two reasons:
//
//   - the interesting failure modes of a filtered, paginated grid are arithmetic (a page that no
//     longer exists after a filter narrows the list; a sort that has to decide where an item with no
//     date goes), and those are worth testing without a DOM — the same split the clip model uses
//     (player/clipModel.ts);
//   - it puts the "a missing field must never hide content" rule in ONE place. Every field but
//     contentType/fileName/filePath is optional on the wire (see ipc/protocol.ts), so each helper
//     below has to answer for the absent case, and the answers are visible together here.
//
// THE ONE INVARIANT: a page is never wrongly empty. Concretely — `deriveLibrary` clamps the requested
// page into the range that actually exists, so a stale page number (the list shrank under the user,
// a `content` push removed items, a filter narrowed the match set) shows the last real page instead
// of a blank grid; and the caller can always tell "no content at all" (`totalCount === 0`) from
// "nothing matches these filters" (`matchCount === 0 && filtered`), which is the difference between a
// fresh install and a broken backend.

import type { ContentItem } from '../../ipc/protocol';
import { formatTime } from '../player/timelineModel';

// ---------------------------------------------------------------------------
// The query
// ---------------------------------------------------------------------------

/** The type dimension. `sessions` is everything that is not a clip — see `matchesType`. */
export type ContentTypeFilter = 'all' | 'sessions' | 'clips';

/** The date dimension: a trailing window, or everything. */
export type DateRangeFilter = 'any' | 'day' | 'week' | 'month' | 'year';

/** The sort dimension. The backend already returns newest-first; this is a client-side reorder. */
export type LibrarySort = 'newest' | 'oldest' | 'game';

/**
 * Sentinels for the game select. They are deliberately not plausible game names (a game called
 * `__any_game__` would collide, and no game is called that) so the select's value can stay a plain
 * string — which is what a `<select>` deals in — without a parallel "is this a real game" flag.
 */
export const ANY_GAME = '__any_game__';
export const NO_GAME = '__no_game__';

/** What a card shows where the game would be, when the item has none. */
export const UNKNOWN_GAME_LABEL = 'Unknown game';

/** What a card shows where the date would be, when the item has none. */
export const UNKNOWN_DATE_LABEL = 'No date';

/**
 * The page size. Twelve suits the grid: it divides by 2, 3 and 4, so every column count the
 * responsive grid can settle on fills its last row.
 */
export const DEFAULT_PAGE_SIZE = 12;

export interface LibraryQuery {
  type: ContentTypeFilter;
  /** A game name, or `ANY_GAME` / `NO_GAME`. */
  game: string;
  range: DateRangeFilter;
  /** Free text over the title, file name and game. Untrimmed — the user's raw input. */
  search: string;
  sort: LibrarySort;
  /** 1-based. May be out of range; `deriveLibrary` clamps it rather than trusting it. */
  page: number;
  pageSize: number;
}

export const DEFAULT_LIBRARY_QUERY: LibraryQuery = {
  type: 'all',
  game: ANY_GAME,
  range: 'any',
  search: '',
  sort: 'newest',
  page: 1,
  pageSize: DEFAULT_PAGE_SIZE,
};

/** The trailing window each date filter means, in seconds. `null` is "no window at all". */
export const DATE_RANGE_SECONDS: Record<DateRangeFilter, number | null> = {
  any: null,
  day: 24 * 60 * 60,
  week: 7 * 24 * 60 * 60,
  month: 30 * 24 * 60 * 60,
  year: 365 * 24 * 60 * 60,
};

// ---------------------------------------------------------------------------
// Reading the optional fields off an item
// ---------------------------------------------------------------------------

/**
 * The item's game, or null when it has none.
 *
 * Three shapes mean "unknown" and all three are seen on the wire: the field absent (an older
 * backend), explicitly null (a backend that looked and found no game), and empty/whitespace (a
 * metadata record with a blank field). They are collapsed here so no caller has to know that.
 */
export function itemGame(item: ContentItem): string | null {
  const game = item.game;
  if (typeof game !== 'string') {
    return null;
  }
  const trimmed = game.trim();
  return trimmed.length > 0 ? trimmed : null;
}

/**
 * The item's start time in epoch seconds, or undefined when it has none.
 *
 * Epoch 0 counts as "none": it is what a record written without a clock carries, and dating a
 * recording to 1970 is worse than admitting the date is unknown. Non-finite values (a malformed
 * record) go the same way — a NaN would otherwise pass every date comparison silently, since every
 * comparison against NaN is false.
 */
export function itemDate(item: ContentItem): number | undefined {
  const start = item.startTime;
  return typeof start === 'number' && Number.isFinite(start) && start > 0 ? start : undefined;
}

/**
 * The item's length in seconds, or undefined when nothing declares one.
 *
 * `durationSeconds` is the metadata record's own field; `endTime` is the older way the same number
 * arrives for a recording. Both are DECLARED lengths — they have been observed overstating the file
 * (player/clipModel.ts documents a record claiming 100s for a 9.13s file), which is exactly why this
 * feeds a chip and nothing else. No clip bound is ever derived from it.
 */
export function itemDuration(item: ContentItem): number | undefined {
  for (const candidate of [item.durationSeconds, item.endTime]) {
    if (typeof candidate === 'number' && Number.isFinite(candidate) && candidate > 0) {
      return candidate;
    }
  }
  return undefined;
}

/** The item's display name: its title, or the file name it was saved under. */
export function itemLabel(item: ContentItem): string {
  const title = item.title?.trim();
  return title && title.length > 0 ? title : item.fileName;
}

/** The human label for an item's content type, for the type chip on a card. */
export function typeLabel(item: ContentItem): string {
  switch (item.contentType) {
    case 'clip':
      return 'Clip';
    case 'highlight':
      return 'Highlight';
    case 'buffer':
      return 'Buffer';
    default:
      return 'Session';
  }
}

// ---------------------------------------------------------------------------
// Formatting (display-only; kept here so the card stays declarative)
// ---------------------------------------------------------------------------

/** The duration chip, or null when no length is declared (the chip is then not rendered). */
export function formatDurationChip(item: ContentItem): string | null {
  const seconds = itemDuration(item);
  return seconds === undefined ? null : formatTime(seconds);
}

/**
 * The date chip. Locale-formatted, short form, and `UNKNOWN_DATE_LABEL` when there is no date —
 * never a blank chip, because a blank one reads as a rendering bug rather than as missing metadata.
 */
export function formatDateChip(item: ContentItem): string {
  const seconds = itemDate(item);
  if (seconds === undefined) {
    return UNKNOWN_DATE_LABEL;
  }
  return new Date(seconds * 1000).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  });
}

/** The file-size chip, or null when the backend does not report a size. Binary units (1 KB = 1024 B). */
export function formatSizeChip(item: ContentItem): string | null {
  const bytes = item.fileSizeBytes;
  if (typeof bytes !== 'number' || !Number.isFinite(bytes) || bytes <= 0) {
    return null;
  }
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  // Sub-10 values keep a decimal ("1.2 GB"); above that the decimal is noise ("335 MB").
  const rounded = unit === 0 || value >= 10 ? Math.round(value) : Math.round(value * 10) / 10;
  return `${rounded} ${units[unit]}`;
}

// ---------------------------------------------------------------------------
// Filtering
// ---------------------------------------------------------------------------

/**
 * The type dimension.
 *
 * `sessions` is "not a clip" rather than "contentType === 'recording'", deliberately. The wire has
 * four content types and two of them (highlight, buffer) belong to neither name the user is offered;
 * defining sessions as the complement of clips keeps `All` the exact union of the two filters, so no
 * item can be invisible under every filter — which is the one way a type filter can lose content.
 */
export function matchesType(item: ContentItem, filter: ContentTypeFilter): boolean {
  if (filter === 'all') {
    return true;
  }
  const isClip = item.contentType === 'clip';
  return filter === 'clips' ? isClip : !isClip;
}

/** The game dimension. `NO_GAME` selects the items whose game is unknown. */
export function matchesGame(item: ContentItem, game: string): boolean {
  if (game === ANY_GAME) {
    return true;
  }
  const itsGame = itemGame(item);
  if (game === NO_GAME) {
    return itsGame === null;
  }
  return itsGame !== null && itsGame.toLowerCase() === game.toLowerCase();
}

/**
 * The date dimension: is the item inside the trailing window ending now?
 *
 * An item with no date does NOT match a window, and that is a real decision rather than an oversight:
 * a window is a claim about when something happened, and an undated item cannot support it. The cost
 * is that a library of undated items looks empty under "Last 7 days" — which is why the caller's
 * empty state says "nothing matches these filters" and offers to clear them.
 *
 * Times in the future are kept (`>= cutoff` has no upper bound): a skewed clock on the recording
 * machine must not make a recording disappear.
 */
export function matchesDate(item: ContentItem, range: DateRangeFilter, nowSeconds: number): boolean {
  const window = DATE_RANGE_SECONDS[range];
  if (window === null || window === undefined) {
    return true;
  }
  const date = itemDate(item);
  if (date === undefined) {
    return false;
  }
  return date >= nowSeconds - window;
}

/**
 * The free-text dimension: a case-insensitive substring of the title, the file name or the game.
 *
 * The file name is searched as well as the title because an item with no metadata record has only a
 * file name — searching just titles would make exactly the items with the least metadata the hardest
 * to find.
 */
export function matchesSearch(item: ContentItem, search: string): boolean {
  const needle = search.trim().toLowerCase();
  if (needle.length === 0) {
    return true;
  }
  const haystack = [item.title, item.fileName, itemGame(item)]
    .filter((part): part is string => typeof part === 'string')
    .join(' ')
    .toLowerCase();
  return haystack.includes(needle);
}

/** Every dimension at once, in the order that rejects cheapest-first. */
export function filterItems(
  items: readonly ContentItem[],
  query: LibraryQuery,
  nowSeconds: number,
): ContentItem[] {
  return items.filter(
    (item) =>
      matchesType(item, query.type) &&
      matchesGame(item, query.game) &&
      matchesDate(item, query.range, nowSeconds) &&
      matchesSearch(item, query.search),
  );
}

/**
 * Whether the query is narrowing the list at all — i.e. whether an empty result is explainable by
 * the filters. The sort is not a filter and is deliberately excluded: reordering cannot empty a grid.
 */
export function isFiltered(query: LibraryQuery): boolean {
  return (
    query.type !== 'all' ||
    query.game !== ANY_GAME ||
    query.range !== 'any' ||
    query.search.trim().length > 0
  );
}

// ---------------------------------------------------------------------------
// The game options
// ---------------------------------------------------------------------------

/**
 * The games present in the list, plus whether anything has no game.
 *
 * Derived from the items rather than from a hardcoded list: the set of games is whatever the user has
 * actually recorded, and a fixed list would both miss games and offer ones that match nothing. The
 * caller turns this into the select's options, so `hasUnknown` is what decides whether an
 * "Unknown game" option is worth offering at all.
 *
 * Names are compared case-insensitively (a game detected twice with different casing is one game) and
 * the first spelling seen wins, so the option label matches what the cards show.
 */
export function availableGames(items: readonly ContentItem[]): {
  names: string[];
  hasUnknown: boolean;
} {
  const seen = new Map<string, string>();
  let hasUnknown = false;
  for (const item of items) {
    const game = itemGame(item);
    if (game === null) {
      hasUnknown = true;
      continue;
    }
    const key = game.toLowerCase();
    if (!seen.has(key)) {
      seen.set(key, game);
    }
  }
  const names = [...seen.values()].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
  return { names, hasUnknown };
}

// ---------------------------------------------------------------------------
// Sorting
// ---------------------------------------------------------------------------

/**
 * Reorder the list. Never mutates the input (a `content` push's array is shared with the source).
 *
 * Undated items sort LAST under every date order, including "oldest first". An undated item is not
 * "infinitely old", it is unknown, and floating unknowns to the top of "oldest" would bury the actual
 * oldest recordings behind them. Ties break on the label so the order is total — an unstable order is
 * visible as cards jumping between pages when the list is re-derived.
 */
export function sortItems(items: readonly ContentItem[], sort: LibrarySort): ContentItem[] {
  const byLabel = (a: ContentItem, b: ContentItem) =>
    itemLabel(a).localeCompare(itemLabel(b), undefined, { sensitivity: 'base' });

  const byDate = (a: ContentItem, b: ContentItem, direction: 1 | -1) => {
    const da = itemDate(a);
    const db = itemDate(b);
    if (da === undefined && db === undefined) {
      return byLabel(a, b);
    }
    if (da === undefined) {
      return 1;
    }
    if (db === undefined) {
      return -1;
    }
    return da === db ? byLabel(a, b) : (da - db) * direction;
  };

  const compare = (a: ContentItem, b: ContentItem): number => {
    if (sort === 'oldest') {
      return byDate(a, b, 1);
    }
    if (sort === 'game') {
      const ga = itemGame(a);
      const gb = itemGame(b);
      if (ga !== gb) {
        // Unknown-game items go last, for the same reason undated ones do.
        if (ga === null) {
          return 1;
        }
        if (gb === null) {
          return -1;
        }
        const byGame = ga.localeCompare(gb, undefined, { sensitivity: 'base' });
        if (byGame !== 0) {
          return byGame;
        }
      }
      // Within one game, newest first — the same default the date sort uses.
      return byDate(a, b, -1);
    }
    return byDate(a, b, -1);
  };

  return [...items].sort(compare);
}

// ---------------------------------------------------------------------------
// Pagination
// ---------------------------------------------------------------------------

/** How many pages `total` items fill. Always at least 1: an empty grid is still page 1 of 1. */
export function pageCountFor(total: number, pageSize: number): number {
  const size = normalizePageSize(pageSize);
  return Math.max(1, Math.ceil(Math.max(0, total) / size));
}

/**
 * Bring a page number into [1, pageCount].
 *
 * This is the second half of the "a page is never wrongly empty" guarantee. The view resets to page 1
 * whenever a filter changes, which handles the interactive path; this handles every other way a page
 * number can go stale — a `content` push that removed items, a filter change the view forgot to reset
 * on, a page number restored from somewhere. The user lands on the last real page instead of a blank
 * grid with working Prev button, which is the shape of the bug this prevents.
 */
export function clampPage(page: number, pageCount: number): number {
  const pages = Math.max(1, Math.floor(pageCount) || 1);
  if (!Number.isFinite(page)) {
    return 1;
  }
  return Math.min(Math.max(1, Math.floor(page)), pages);
}

function normalizePageSize(pageSize: number): number {
  return Number.isFinite(pageSize) && pageSize >= 1 ? Math.floor(pageSize) : DEFAULT_PAGE_SIZE;
}

/** What the grid renders: one page of items plus everything the surrounding chrome needs. */
export interface LibraryPage {
  /** The items on the resolved page, in sort order. */
  items: ContentItem[];
  /** How many items matched the filters, before pagination. */
  matchCount: number;
  /** How many items exist at all — `0` is "no content", not "nothing matches". */
  totalCount: number;
  /** The page actually shown, 1-based and guaranteed to exist. */
  page: number;
  pageCount: number;
  /** 1-based position of the first / last item shown; both 0 when nothing matched. */
  firstIndex: number;
  lastIndex: number;
  /** Whether the filters are narrowing the list — drives which empty state is shown. */
  filtered: boolean;
}

/**
 * The whole pipeline: filter, sort, then take the requested page (clamped into existence).
 *
 * `nowSeconds` is injected rather than read from the clock so the date window is deterministic — the
 * caller passes `Date.now() / 1000`, a test passes a fixed epoch.
 */
export function deriveLibrary(
  items: readonly ContentItem[],
  query: LibraryQuery,
  nowSeconds: number,
): LibraryPage {
  const matched = sortItems(filterItems(items, query, nowSeconds), query.sort);
  const pageSize = normalizePageSize(query.pageSize);
  const pageCount = pageCountFor(matched.length, pageSize);
  const page = clampPage(query.page, pageCount);
  const start = (page - 1) * pageSize;
  const pageItems = matched.slice(start, start + pageSize);
  return {
    items: pageItems,
    matchCount: matched.length,
    totalCount: items.length,
    page,
    pageCount,
    firstIndex: pageItems.length === 0 ? 0 : start + 1,
    lastIndex: pageItems.length === 0 ? 0 : start + pageItems.length,
    filtered: isFiltered(query),
  };
}
