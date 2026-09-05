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
import { contentTypeLabel, formatContentDuration, formatContentSize } from '../contentPresentation';

// ---------------------------------------------------------------------------
// The query
// ---------------------------------------------------------------------------

/** The type dimension. `sessions` is everything that is not a clip — see `matchesType`. */
export type ContentTypeFilter = 'all' | 'sessions' | 'clips' | 'highlights' | 'trash';

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
  favoriteOnly: boolean;
}

export const DEFAULT_LIBRARY_QUERY: LibraryQuery = {
  type: 'all',
  game: ANY_GAME,
  range: 'any',
  search: '',
  sort: 'newest',
  page: 1,
  pageSize: DEFAULT_PAGE_SIZE,
  favoriteOnly: false,
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
 * The item's game, or null when it has none. Three shapes mean "unknown" and all three are seen on
 * the wire: the field absent (an older backend), explicitly null (a backend that looked and found
 * no game), and empty/whitespace (a metadata record with a blank field).
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
 * The item's start time in epoch seconds, or undefined when it has none. Epoch 0 counts as "none":
 * it is what a record written without a clock carries, and dating a recording to 1970 is worse than
 * admitting the date is unknown.
 */
export function itemDate(item: ContentItem): number | undefined {
  const start = item.startTime;
  return typeof start === 'number' && Number.isFinite(start) && start > 0 ? start : undefined;
}

/**
 * The item's length in seconds, or undefined when nothing declares one. `durationSeconds` is the
 * metadata record's own field; `endTime` is the older way the same number arrives for a recording.
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
  return contentTypeLabel(item.contentType);
}

// ---------------------------------------------------------------------------
// Grouping — a recording and the clips cut from it
// ---------------------------------------------------------------------------

/** A recording with the clips cut from it. `recording` is null for clips whose source is gone. */
export interface RecordingGroup {
  recording: ContentItem | null;
  clips: ContentItem[];
}

export function isClipContent(item: ContentItem): boolean {
  return item.contentType === 'clip' || item.contentType === 'highlight';
}

/** Automatic highlights linked to one recording, in their order on its timeline. */
export function linkedAutomaticHighlights(
  recording: ContentItem,
  candidates: readonly ContentItem[],
): ContentItem[] {
  return candidates
    .filter(
      (item) => item.automated === true && item.sourceSessionPath === recording.filePath,
    )
    .sort(
      (left, right) =>
        (left.clipStartTime ?? Number.POSITIVE_INFINITY) -
        (right.clipStartTime ?? Number.POSITIVE_INFINITY),
    );
}

/**
 * The automatic highlights a session deletion would cascade to: automated, linked to the exact
 * recording, and not favourited. This mirrors the backend's eligibility rule (AppHost.DeleteOne) so
 * the confirmation can count what the trash sentence promises.
 */
export function cascadableLinkedHighlights(
  recording: ContentItem,
  candidates: readonly ContentItem[],
): ContentItem[] {
  return linkedAutomaticHighlights(recording, candidates).filter(
    (item) => item.favorite !== true,
  );
}

/** The file name without its extension, which is what the clip→recording link is written in. */
function baseName(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  return dot <= 0 ? fileName : fileName.slice(0, dot);
}

/**
 * Which recording a clip was cut from, or null when it has outlived its source.
 *
 * Nothing on the wire carries the link — a clip has no metadata record of its own — so the file name
 * is it: `CreateClip` starts a clip's name with the source recording's base name. This is the same
 * rule the backend applies in `AppHost.InheritedFrom`, character for character, because a clip
 * inherits its game and audio-track names through it. If the two disagreed, a clip would show one
 * recording's game while sitting under another's. The longest match wins, so a recording whose name
 * is a prefix of another cannot claim its clips, and the character after the prefix must be the
 * separator, so `ow` cannot claim `owl-01`.
 */
function sourceOf(clip: ContentItem, recordings: readonly ContentItem[]): ContentItem | null {
  if (clip.sourceSessionPath) {
    return recordings.find((recording) => recording.filePath === clip.sourceSessionPath) ?? null;
  }

  const clipBase = baseName(clip.fileName);
  let source: ContentItem | null = null;
  let matched = 0;

  for (const recording of recordings) {
    const recordingBase = baseName(recording.fileName);
    if (recordingBase.length <= matched) continue;
    if (!clipBase.startsWith(recordingBase)) continue;
    if (clipBase.length !== recordingBase.length && clipBase[recordingBase.length] !== '-') continue;
    source = recording;
    matched = recordingBase.length;
  }

  return source;
}

export function sourceSession(item: ContentItem, candidates: readonly ContentItem[]): ContentItem | null {
  if (item.contentType === 'recording') {
    return item;
  }
  if (!isClipContent(item)) {
    return null;
  }
  return sourceOf(item, candidates.filter((candidate) => candidate.contentType === 'recording'));
}

export function sessionPlaylist(recording: ContentItem, candidates: readonly ContentItem[]): ContentItem[] {
  const children = candidates
    .filter((item) => isClipContent(item) && sourceOf(item, [recording]) !== null)
    .sort((left, right) => {
      const byTimeline = (left.clipStartTime ?? Number.POSITIVE_INFINITY)
        - (right.clipStartTime ?? Number.POSITIVE_INFINITY);
      return byTimeline !== 0 ? byTimeline : itemLabel(left).localeCompare(itemLabel(right));
    });
  const playableMain = recording.videoMissing !== true
    && recording.highlightsOnly !== true
    && recording.recording !== true;
  return playableMain ? [recording, ...children] : children;
}

/**
 * True for a session with no main video: the backend lists it as a synthetic placeholder —
 * `videoMissing` when the source file is gone, `highlightsOnly` when the session is made only of
 * its linked highlights. Its clips and highlights are still real items; the session itself cannot
 * be opened in the player.
 */
export function lacksMainVideo(item: ContentItem): boolean {
  return !isClipContent(item) && (item.videoMissing === true || item.highlightsOnly === true);
}

/**
 * The clip/highlight counts a session card badges. One pass over the whole list so the badge is
 * stable whether the card sits in a group, a flat grid or the sessions page.
 */
export function recordingChildCounts(items: readonly ContentItem[]): Map<string, { clips: number; highlights: number }> {
  const counts = new Map<string, { clips: number; highlights: number }>();
  for (const group of groupByRecording(items)) {
    if (!group.recording) continue;
    counts.set(group.recording.filePath, {
      clips: group.clips.length,
      highlights: group.clips.filter((clip) => clip.automated).length,
    });
  }
  return counts;
}

/**
 * The linked highlights a session card previews in place of its own video: no main video at all,
 * or a session still being written.
 */
export function sessionPreviewHighlights(
  item: ContentItem,
  items: readonly ContentItem[],
): ContentItem[] | undefined {
  return item.videoMissing === true || item.highlightsOnly === true || item.recording === true
    ? linkedAutomaticHighlights(item, items)
    : undefined;
}

/**
 * The content list as recordings with their clips, **in the order they arrived**.
 *
 * Ordering is the caller's: `deriveGroupedLibrary` hands in an already-sorted list, so the sort
 * control keeps working. Sorting here as well would silently override "oldest first".
 *
 * Every item reaches exactly one group. A clip whose recording is missing — deleted, or filtered out
 * of the list handed in — heads its own group under `recording: null` rather than disappearing,
 * which is the library's one invariant (see the header) applied to grouping. It heads a group of its
 * own rather than joining a trailing catch-all so that it stays in date order: a clip outliving its
 * recording is not something the user can act on, and burying it at the end helps nobody.
 */
export function groupByRecording(items: readonly ContentItem[]): RecordingGroup[] {
  const recordings = items.filter((item) => !isClipContent(item));
  const groups = new Map<ContentItem, RecordingGroup>();
  const ordered: RecordingGroup[] = [];

  // Heads first, so every group sits where its head sat in the caller's order.
  for (const item of items) {
    if (!isClipContent(item)) {
      const group: RecordingGroup = { recording: item, clips: [] };
      groups.set(item, group);
      ordered.push(group);
    } else if (sourceOf(item, recordings) === null) {
      ordered.push({ recording: null, clips: [item] });
    }
  }

  for (const item of items) {
    if (!isClipContent(item)) continue;
    const source = sourceOf(item, recordings);
    if (source !== null) {
      groups.get(source)!.clips.push(item);
    }
  }

  return ordered;
}

// ---------------------------------------------------------------------------
// Formatting (display-only; kept here so the card stays declarative)
// ---------------------------------------------------------------------------

/** The duration chip, or null when no length is declared (the chip is then not rendered). */
export function formatDurationChip(item: ContentItem): string | null {
  const seconds = itemDuration(item);
  return formatContentDuration(seconds);
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
  return formatContentSize(item.fileSizeBytes);
}

/** A byte count as a size chip, or null when there is no usable number. Shared with the trash list. */
export function formatBytes(bytes: number | undefined): string | null {
  return formatContentSize(bytes);
}

// ---------------------------------------------------------------------------
// Filtering
// ---------------------------------------------------------------------------

/**
 * The type dimension. `sessions` is "not a clip" rather than "contentType === 'recording'",
 * deliberately.
 */
export function matchesType(item: ContentItem, filter: ContentTypeFilter): boolean {
  if (filter === 'all') {
    return true;
  }
  if (filter === 'clips') {
    return item.contentType === 'clip';
  }
  if (filter === 'highlights') {
    return item.contentType === 'highlight';
  }
  return !isClipContent(item);
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
 * The date dimension: is the item inside the trailing window ending now? An item with no date does
 * NOT match a window, and that is a real decision rather than an oversight: a window is a claim
 * about when something happened, and an undated item cannot support it.
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
 * The file name is searched as well as the title because an item with no metadata record has only a
 * file name — searching just titles would make exactly the items with the least metadata the
 * hardest to find.
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
      // The library's item views list actual playable items: a placeholder session has no video
      // to open, so it stays out of the library's Sessions list. The top-level sessions page and
      // the trash (a separate filter path) still show it.
      (query.type !== 'sessions' || !lacksMainVideo(item)) &&
      matchesGame(item, query.game) &&
      matchesDate(item, query.range, nowSeconds) &&
      (!query.favoriteOnly || item.favorite === true) &&
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
    || query.favoriteOnly
  );
}

// ---------------------------------------------------------------------------
// The game options
// ---------------------------------------------------------------------------

/**
 * The games present in the list, plus whether anything has no game. Derived from the items rather
 * than from a hardcoded list: the set of games is whatever the user has actually recorded, and a
 * fixed list would both miss games and offer ones that match nothing.
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

/** Reorder the list. Never mutates the input (a `content` push's array is shared with the source). */
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
 * Bring a page number into [1, pageCount]. This is the second half of the "a page is never wrongly
 * empty" guarantee.
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

/** What the grouped view renders: one page of recordings-with-their-clips. */
export interface LibraryGroupPage {
  /** The groups on the resolved page, in sort order. */
  groups: RecordingGroup[];
  /** The complete filtered and sorted result set, flat, used by player navigation. */
  resultItems: ContentItem[];
  matchCount: number;
  totalCount: number;
  /** How many groups matched, which is what the page count is over. */
  groupCount: number;
  page: number;
  pageCount: number;
  filtered: boolean;
}

/**
 * The grouped pipeline: filter, sort, group, then take the requested page **of groups**.
 *
 * Pagination is over groups rather than items on purpose. Paginating items and grouping the page
 * would cut a recording away from its own clips whenever the boundary fell between them — the group
 * would render headless on one page and clipless on the other, and nothing in the UI would say why.
 * The cost is that a page holds a variable number of items, which the count line states plainly.
 */
export function deriveGroupedLibrary(
  items: readonly ContentItem[],
  query: LibraryQuery,
  nowSeconds: number,
): LibraryGroupPage {
  const matched = sortItems(filterItems(items, query, nowSeconds), query.sort);
  const groups = groupByRecording(matched);
  const pageSize = normalizePageSize(query.pageSize);
  const pageCount = pageCountFor(groups.length, pageSize);
  const page = clampPage(query.page, pageCount);
  const start = (page - 1) * pageSize;
  return {
    groups: groups.slice(start, start + pageSize),
    resultItems: matched,
    matchCount: matched.length,
    totalCount: items.length,
    groupCount: groups.length,
    page,
    pageCount,
    filtered: isFiltered(query),
  };
}

/** What the grid renders: one page of items plus everything the surrounding chrome needs. */
export interface LibraryPage {
  /** The items on the resolved page, in sort order. */
  items: ContentItem[];
  /** The complete filtered and sorted result set, used by player navigation. */
  resultItems: ContentItem[];
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
 * `nowSeconds` is injected rather than read from the clock so the date window is deterministic —
 * the caller passes `Date.now() / 1000`, a test passes a fixed epoch.
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
    resultItems: matched,
    matchCount: matched.length,
    totalCount: items.length,
    page,
    pageCount,
    firstIndex: pageItems.length === 0 ? 0 : start + 1,
    lastIndex: pageItems.length === 0 ? 0 : start + pageItems.length,
    filtered: isFiltered(query),
  };
}

/** The date options the filter rows offer, shared by the library and the sessions page. */
export const DATE_OPTIONS: { value: string; label: string }[] = [
  { value: 'any', label: 'Any time' },
  { value: 'day', label: 'Last 24 hours' },
  { value: 'week', label: 'Last 7 days' },
  { value: 'month', label: 'Last 30 days' },
  { value: 'year', label: 'Last year' },
];

/** The sort options the filter rows offer, shared by the library and the sessions page. */
export const SORT_OPTIONS: { value: string; label: string }[] = [
  { value: 'newest', label: 'Newest first' },
  { value: 'oldest', label: 'Oldest first' },
  { value: 'game', label: 'Game (A–Z)' },
];

/**
 * The top-level sessions page: every session — including the main-video-less placeholders the
 * library's item views hide, and the session still being written — through the same filter, sort
 * and pagination pipeline. The type dimension is the page itself, so it takes no part in
 * `filtered`: with every other filter cleared, an empty page means "no sessions", not "filtered out".
 */
export function deriveSessions(
  items: readonly ContentItem[],
  query: LibraryQuery,
  nowSeconds: number,
): LibraryPage {
  const sessions = items.filter((item) => !isClipContent(item));
  const matched = sortItems(
    sessions.filter(
      (item) =>
        matchesGame(item, query.game) &&
        matchesDate(item, query.range, nowSeconds) &&
        (!query.favoriteOnly || item.favorite === true) &&
        matchesSearch(item, query.search),
    ),
    query.sort,
  );
  const pageSize = normalizePageSize(query.pageSize);
  const pageCount = pageCountFor(matched.length, pageSize);
  const page = clampPage(query.page, pageCount);
  const start = (page - 1) * pageSize;
  const pageItems = matched.slice(start, start + pageSize);
  return {
    items: pageItems,
    resultItems: matched,
    matchCount: matched.length,
    totalCount: sessions.length,
    page,
    pageCount,
    firstIndex: pageItems.length === 0 ? 0 : start + 1,
    lastIndex: pageItems.length === 0 ? 0 : start + pageItems.length,
    filtered:
      query.game !== ANY_GAME ||
      query.range !== 'any' ||
      query.search.trim().length > 0 ||
      query.favoriteOnly,
  };
}
