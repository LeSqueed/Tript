// SPDX-License-Identifier: GPL-2.0-or-later
//
// The library derivation model, tested without a DOM. Two things are being pinned down here.

import { describe, expect, it } from 'vitest';
import type { ContentItem } from '../../ipc/protocol';
import {
  deriveGroupedLibrary,
  groupByRecording,
  ANY_GAME,
  availableGames,
  clampPage,
  DEFAULT_LIBRARY_QUERY,
  deriveLibrary,
  filterItems,
  formatDateChip,
  formatDurationChip,
  formatSizeChip,
  isFiltered,
  itemDate,
  itemDuration,
  itemGame,
  itemLabel,
  matchesDate,
  matchesGame,
  matchesSearch,
  matchesType,
  NO_GAME,
  pageCountFor,
  sortItems,
  typeLabel,
  UNKNOWN_DATE_LABEL,
  UNKNOWN_GAME_LABEL,
  type LibraryQuery,
} from './libraryModel';

/** A fixed "now": 2026-08-17T00:00:00Z in epoch seconds. */
const NOW = 1787011200;
const HOUR = 3600;
const DAY = 24 * HOUR;

function item(overrides: Partial<ContentItem> & { fileName: string }): ContentItem {
  return {
    contentType: 'recording',
    filePath: `sessions/${overrides.fileName}`,
    ...overrides,
  };
}

const recentSession = item({
  fileName: 'cs2-today.mp4',
  title: 'Ranked win',
  game: 'Counter-Strike 2',
  startTime: NOW - 2 * HOUR,
  durationSeconds: 1830,
  fileSizeBytes: 1_500_000_000,
});

const oldSession = item({
  fileName: 'rl-old.mp4',
  title: 'Old ranked',
  game: 'Rocket League',
  startTime: NOW - 60 * DAY,
  durationSeconds: 600,
});

const clip = item({
  contentType: 'clip',
  fileName: 'clip-1.mp4',
  filePath: 'clips/clip-1.mp4',
  title: 'Nice shot',
  game: 'Rocket League',
  startTime: NOW - 3 * DAY,
  durationSeconds: 3.083333,
  fileSizeBytes: 33338,
});

/** The shape of an item with no metadata record: a file name, and nothing else. */
const bareSession = item({ fileName: 'session-bare.mp4' });

const ALL = [recentSession, clip, oldSession, bareSession];

function query(overrides: Partial<LibraryQuery> = {}): LibraryQuery {
  return { ...DEFAULT_LIBRARY_QUERY, ...overrides };
}

describe('reading the optional wire fields', () => {
  it('treats an absent, null or blank game as unknown', () => {
    expect(itemGame(recentSession)).toBe('Counter-Strike 2');
    expect(itemGame(bareSession)).toBeNull();
    expect(itemGame(item({ fileName: 'a.mp4', game: null }))).toBeNull();
    expect(itemGame(item({ fileName: 'a.mp4', game: '   ' }))).toBeNull();
  });

  it('treats epoch 0 and a non-finite start time as no date at all', () => {
    // Epoch 0 is what a record written without a clock carries — dating it to 1970 would be a lie
    // dressed up as data.
    expect(itemDate(item({ fileName: 'a.mp4', startTime: 0 }))).toBeUndefined();
    expect(itemDate(item({ fileName: 'a.mp4', startTime: Number.NaN }))).toBeUndefined();
    expect(itemDate(recentSession)).toBe(NOW - 2 * HOUR);
  });

  it('takes the duration from durationSeconds, falling back to endTime', () => {
    expect(itemDuration(clip)).toBeCloseTo(3.083333);
    expect(itemDuration(item({ fileName: 'a.mp4', endTime: 120 }))).toBe(120);
    // durationSeconds wins when both are present.
    expect(itemDuration(item({ fileName: 'a.mp4', durationSeconds: 10, endTime: 120 }))).toBe(10);
    expect(itemDuration(bareSession)).toBeUndefined();
    expect(itemDuration(item({ fileName: 'a.mp4', durationSeconds: 0 }))).toBeUndefined();
  });

  it('labels an item by its title, falling back to the file name', () => {
    expect(itemLabel(recentSession)).toBe('Ranked win');
    expect(itemLabel(bareSession)).toBe('session-bare.mp4');
    expect(itemLabel(item({ fileName: 'a.mp4', title: '  ' }))).toBe('a.mp4');
  });

  it('names every content type, including the two the filters do not mention', () => {
    expect(typeLabel(recentSession)).toBe('Recording');
    expect(typeLabel(clip)).toBe('Clip');
    expect(typeLabel(item({ fileName: 'h.mp4', contentType: 'highlight' }))).toBe('Highlight');
    expect(typeLabel(item({ fileName: 'b.mp4', contentType: 'buffer' }))).toBe('Buffer');
  });
});

describe('chip formatting', () => {
  it('formats a duration and omits the chip when none is declared', () => {
    expect(formatDurationChip(recentSession)).toBe('30:30');
    expect(formatDurationChip(clip)).toBe('0:03');
    expect(formatDurationChip(bareSession)).toBeNull();
  });

  it('says so rather than rendering a blank chip when there is no date', () => {
    expect(formatDateChip(bareSession)).toBe(UNKNOWN_DATE_LABEL);
    expect(formatDateChip(recentSession)).toContain('2026');
  });

  it('formats a size in binary units, and omits the chip when the size is unknown', () => {
    // Above 10 the decimal is noise, below it the decimal is the information.
    expect(formatSizeChip(clip)).toBe('33 KB');
    expect(formatSizeChip(recentSession)).toBe('1.4 GB');
    expect(formatSizeChip(item({ fileName: 'a.mp4', fileSizeBytes: 900 }))).toBe('900 B');
    expect(formatSizeChip(bareSession)).toBeNull();
    expect(formatSizeChip(item({ fileName: 'a.mp4', fileSizeBytes: 0 }))).toBeNull();
  });
});

describe('the type filter', () => {
  it('selects clips, and everything else as sessions', () => {
    expect(matchesType(clip, 'clips')).toBe(true);
    expect(matchesType(recentSession, 'clips')).toBe(false);
    expect(matchesType(recentSession, 'sessions')).toBe(true);
    expect(matchesType(clip, 'sessions')).toBe(false);
  });

  it('leaves no content type invisible: All is exactly Sessions ∪ Clips', () => {
    // highlight and buffer are on the wire but named by neither filter. Defining sessions as "not a
    // clip" is what stops them falling through both.
    for (const contentType of ['recording', 'clip', 'highlight', 'buffer'] as const) {
      const candidate = item({ fileName: `${contentType}.mp4`, contentType });
      expect(matchesType(candidate, 'all')).toBe(true);
      expect(matchesType(candidate, 'sessions') || matchesType(candidate, 'clips')).toBe(true);
      expect(matchesType(candidate, 'sessions') && matchesType(candidate, 'clips')).toBe(false);
    }
  });
});

describe('the game filter', () => {
  it('matches a game case-insensitively and selects unknown-game items with NO_GAME', () => {
    expect(matchesGame(recentSession, ANY_GAME)).toBe(true);
    expect(matchesGame(recentSession, 'counter-strike 2')).toBe(true);
    expect(matchesGame(recentSession, 'Rocket League')).toBe(false);
    expect(matchesGame(bareSession, NO_GAME)).toBe(true);
    expect(matchesGame(recentSession, NO_GAME)).toBe(false);
  });

  it('derives the game options from the items present, flagging the unknowns', () => {
    expect(availableGames(ALL)).toEqual({
      names: ['Counter-Strike 2', 'Rocket League'],
      hasUnknown: true,
    });
    expect(availableGames([recentSession])).toEqual({
      names: ['Counter-Strike 2'],
      hasUnknown: false,
    });
    // One game detected under two spellings is one option, in the spelling first seen.
    expect(
      availableGames([item({ fileName: 'a.mp4', game: 'DOTA 2' }), item({ fileName: 'b.mp4', game: 'dota 2' })]).names,
    ).toEqual(['DOTA 2']);
    expect(availableGames([])).toEqual({ names: [], hasUnknown: false });
  });
});

describe('the date filter', () => {
  it('keeps what falls inside the trailing window', () => {
    expect(matchesDate(recentSession, 'day', NOW)).toBe(true);
    expect(matchesDate(clip, 'day', NOW)).toBe(false);
    expect(matchesDate(clip, 'week', NOW)).toBe(true);
    expect(matchesDate(oldSession, 'month', NOW)).toBe(false);
    expect(matchesDate(oldSession, 'year', NOW)).toBe(true);
  });

  it('keeps everything under "any time", including undated items', () => {
    expect(matchesDate(bareSession, 'any', NOW)).toBe(true);
  });

  it('excludes an undated item from every window, since it cannot support the claim', () => {
    expect(matchesDate(bareSession, 'day', NOW)).toBe(false);
    expect(matchesDate(bareSession, 'year', NOW)).toBe(false);
  });

  it('keeps an item timestamped in the future rather than hiding it', () => {
    // A skewed clock on the recording machine must not make a recording disappear.
    const future = item({ fileName: 'future.mp4', startTime: NOW + DAY });
    expect(matchesDate(future, 'day', NOW)).toBe(true);
  });
});

describe('the search filter', () => {
  it('matches the title, the file name or the game, case-insensitively', () => {
    expect(matchesSearch(recentSession, 'ranked')).toBe(true);
    expect(matchesSearch(recentSession, 'COUNTER')).toBe(true);
    expect(matchesSearch(bareSession, 'session-bare')).toBe(true);
    expect(matchesSearch(recentSession, 'rocket')).toBe(false);
  });

  it('matches everything on empty or whitespace-only input', () => {
    expect(matchesSearch(bareSession, '')).toBe(true);
    expect(matchesSearch(bareSession, '   ')).toBe(true);
  });
});

describe('filterItems', () => {
  it('applies every dimension at once', () => {
    expect(filterItems(ALL, query({ type: 'clips' }), NOW)).toEqual([clip]);
    expect(filterItems(ALL, query({ game: 'Rocket League' }), NOW)).toEqual([clip, oldSession]);
    expect(filterItems(ALL, query({ game: NO_GAME }), NOW)).toEqual([bareSession]);
    expect(filterItems(ALL, query({ range: 'week' }), NOW)).toEqual([recentSession, clip]);
    expect(filterItems(ALL, query({ type: 'sessions', game: 'Rocket League' }), NOW)).toEqual([oldSession]);
    expect(filterItems(ALL, query({ search: 'nice' }), NOW)).toEqual([clip]);
  });

  it('reports whether the query is narrowing anything', () => {
    expect(isFiltered(query())).toBe(false);
    // A sort is not a filter: reordering cannot empty a grid, so it must not offer to be "cleared".
    expect(isFiltered(query({ sort: 'oldest' }))).toBe(false);
    expect(isFiltered(query({ type: 'clips' }))).toBe(true);
    expect(isFiltered(query({ game: NO_GAME }))).toBe(true);
    expect(isFiltered(query({ range: 'day' }))).toBe(true);
    expect(isFiltered(query({ search: ' x ' }))).toBe(true);
    expect(isFiltered(query({ search: '   ' }))).toBe(false);
  });
});

describe('sortItems', () => {
  it('orders newest first by default and oldest first on request', () => {
    expect(sortItems(ALL, 'newest').map(itemLabel)).toEqual([
      'Ranked win',
      'Nice shot',
      'Old ranked',
      'session-bare.mp4',
    ]);
    expect(sortItems(ALL, 'oldest').map(itemLabel)).toEqual([
      'Old ranked',
      'Nice shot',
      'Ranked win',
      'session-bare.mp4',
    ]);
  });

  it('puts undated items last under BOTH date orders', () => {
    // "Unknown" is not "infinitely old": floating undated items to the top of "oldest first" would
    // bury the actual oldest recordings behind them.
    expect(itemLabel(sortItems(ALL, 'oldest').at(-1)!)).toBe('session-bare.mp4');
    expect(itemLabel(sortItems(ALL, 'newest').at(-1)!)).toBe('session-bare.mp4');
  });

  it('groups by game A–Z, newest first within a game, unknown games last', () => {
    expect(sortItems(ALL, 'game').map(itemLabel)).toEqual([
      'Ranked win', // Counter-Strike 2
      'Nice shot', // Rocket League, 3 days ago
      'Old ranked', // Rocket League, 60 days ago
      'session-bare.mp4', // no game
    ]);
  });

  it('never mutates the input array', () => {
    const input = [...ALL];
    sortItems(input, 'oldest');
    expect(input).toEqual(ALL);
  });

  it('breaks ties on the label so the order is total', () => {
    const b = item({ fileName: 'b.mp4', title: 'Bravo', startTime: NOW });
    const a = item({ fileName: 'a.mp4', title: 'Alpha', startTime: NOW });
    expect(sortItems([b, a], 'newest').map(itemLabel)).toEqual(['Alpha', 'Bravo']);
    expect(sortItems([a, b], 'newest').map(itemLabel)).toEqual(['Alpha', 'Bravo']);
  });
});

describe('pagination arithmetic', () => {
  it('counts pages, with an empty list still being page 1 of 1', () => {
    expect(pageCountFor(0, 12)).toBe(1);
    expect(pageCountFor(12, 12)).toBe(1);
    expect(pageCountFor(13, 12)).toBe(2);
    expect(pageCountFor(24, 12)).toBe(2);
  });

  it('clamps a page into the range that exists', () => {
    expect(clampPage(1, 3)).toBe(1);
    expect(clampPage(3, 3)).toBe(3);
    expect(clampPage(9, 3)).toBe(3);
    expect(clampPage(0, 3)).toBe(1);
    expect(clampPage(-2, 3)).toBe(1);
    expect(clampPage(Number.NaN, 3)).toBe(1);
    expect(clampPage(2.7, 3)).toBe(2);
  });
});

describe('deriveLibrary', () => {
  /** 25 dated sessions, newest first — enough for three pages at the default size. */
  const many = Array.from({ length: 25 }, (_, index) =>
    item({
      fileName: `s-${index}.mp4`,
      title: `Session ${index}`,
      game: index < 5 ? 'Counter-Strike 2' : 'Rocket League',
      startTime: NOW - index * HOUR,
    }),
  );

  it('pages the sorted match set and reports the position', () => {
    const page1 = deriveLibrary(many, query({ pageSize: 12 }), NOW);
    expect(page1.items.map(itemLabel)).toEqual(
      Array.from({ length: 12 }, (_, index) => `Session ${index}`),
    );
    expect(page1).toMatchObject({
      page: 1,
      pageCount: 3,
      matchCount: 25,
      totalCount: 25,
      firstIndex: 1,
      lastIndex: 12,
      filtered: false,
    });

    const page3 = deriveLibrary(many, query({ pageSize: 12, page: 3 }), NOW);
    expect(page3.items.map(itemLabel)).toEqual(['Session 24']);
    expect(page3).toMatchObject({ page: 3, firstIndex: 25, lastIndex: 25 });
  });

  it('clamps a page that a narrowing filter has left behind', () => {
    // Page 3 of everything, then only the 5 Counter-Strike items match — page 3 no longer exists, and
    // the answer is the last page that does, NOT an empty grid with a working Prev button.
    const narrowed = deriveLibrary(
      many,
      query({ pageSize: 12, page: 3, game: 'Counter-Strike 2' }),
      NOW,
    );
    expect(narrowed.page).toBe(1);
    expect(narrowed.pageCount).toBe(1);
    expect(narrowed.items).toHaveLength(5);
    expect(narrowed.filtered).toBe(true);
  });

  it('clamps a page the list shrank out from under (a content push, not an interaction)', () => {
    const shrunk = deriveLibrary(many.slice(0, 13), query({ pageSize: 12, page: 3 }), NOW);
    expect(shrunk.page).toBe(2);
    expect(shrunk.items.map(itemLabel)).toEqual(['Session 12']);
  });

  it('distinguishes no content at all from nothing matching the filters', () => {
    const nothing = deriveLibrary([], query(), NOW);
    expect(nothing).toMatchObject({ totalCount: 0, matchCount: 0, filtered: false, pageCount: 1 });
    expect(nothing.firstIndex).toBe(0);
    expect(nothing.lastIndex).toBe(0);

    const noMatch = deriveLibrary(ALL, query({ search: 'nothing matches this' }), NOW);
    expect(noMatch).toMatchObject({ totalCount: 4, matchCount: 0, filtered: true });
  });

  it('filters to persisted favorites without changing the sort order', () => {
    const favorites = deriveLibrary(
      [
        item({ fileName: 'unmarked.mp4', title: 'Unmarked', startTime: NOW - HOUR }),
        item({ fileName: 'favorite.mp4', title: 'Favorite', favorite: true, startTime: NOW }),
      ],
      query({ favoriteOnly: true }),
      NOW,
    );
    expect(favorites.items.map(itemLabel)).toEqual(['Favorite']);
    expect(favorites.filtered).toBe(true);
  });

  it('falls back to the default page size when handed a nonsensical one', () => {
    expect(deriveLibrary(many, query({ pageSize: 0 }), NOW).pageCount).toBe(3);
    expect(deriveLibrary(many, query({ pageSize: Number.NaN }), NOW).pageCount).toBe(3);
  });

  it('renders every item when nothing is filtered — a missing field never hides content', () => {
    // The undated, gameless, sizeless item is on the page like any other.
    const all = deriveLibrary(ALL, query(), NOW);
    expect(all.items).toHaveLength(4);
    expect(all.items.map(itemLabel)).toContain('session-bare.mp4');
    expect(UNKNOWN_GAME_LABEL).toBe('Unknown game');
  });
});

describe('grouping clips under their recording', () => {
  const link = (fileName: string, contentType: ContentItem['contentType']) =>
    item({ fileName, contentType, filePath: `${contentType}s/${fileName}` });

  it('groups a clip under the recording it was cut from', () => {
    const groups = groupByRecording([
      link('session-1.mp4', 'recording'),
      link('session-1-01.mp4', 'clip'),
      link('session-2-01.mp4', 'clip'),
    ]);

    expect(groups).toHaveLength(2);
    expect(groups[0].recording?.fileName).toBe('session-1.mp4');
    expect(groups[0].clips.map((c) => c.fileName)).toEqual(['session-1-01.mp4']);
    // A clip whose recording is gone still lists, under no recording, rather than vanishing.
    expect(groups[1].recording).toBeNull();
    expect(groups[1].clips.map((c) => c.fileName)).toEqual(['session-2-01.mp4']);
  });

  // The server-side rule (AppHost.InheritedFrom) that decides which recording a clip inherits its
  // game and audio tracks from. Grouping has to agree with it, or a clip would show one recording's
  // game while sitting under another's.
  it('gives a clip to the longest matching recording, not the first', () => {
    const groups = groupByRecording([
      link('ow.mp4', 'recording'),
      link('ow-ranked.mp4', 'recording'),
      link('ow-ranked-01.mp4', 'clip'),
    ]);

    const owRanked = groups.find((g) => g.recording?.fileName === 'ow-ranked.mp4');
    expect(owRanked?.clips.map((c) => c.fileName)).toEqual(['ow-ranked-01.mp4']);
    expect(groups.find((g) => g.recording?.fileName === 'ow.mp4')?.clips).toEqual([]);
  });

  it('does not let a recording claim a clip that merely starts with its name', () => {
    const groups = groupByRecording([
      link('ow.mp4', 'recording'),
      link('owl-01.mp4', 'clip'),
    ]);

    expect(groups.find((g) => g.recording?.fileName === 'ow.mp4')?.clips).toEqual([]);
    expect(groups.find((g) => g.recording === null)?.clips.map((c) => c.fileName)).toEqual([
      'owl-01.mp4',
    ]);
  });

  // Order is the caller's — deriveGroupedLibrary sorts before grouping, so the sort control keeps
  // working. Sorting here as well would silently override "oldest first".
  it('keeps a recording with no clips, and preserves the order it was given', () => {
    const groups = groupByRecording([
      item({ fileName: 'old.mp4', startTime: NOW - DAY }),
      item({ fileName: 'new.mp4', startTime: NOW }),
    ]);

    expect(groups.map((g) => g.recording?.fileName)).toEqual(['old.mp4', 'new.mp4']);
    expect(groups[0].clips).toEqual([]);
  });

  // "Not a clip" heads its own group, the same rule matchesType draws. A buffer save is a recording
  // in its own right, not a cut from one, even when its name happens to share a prefix.
  it('never absorbs a non-clip into another group', () => {
    const groups = groupByRecording([
      link('session-1.mp4', 'recording'),
      link('session-1-01.mp4', 'buffer'),
    ]);

    expect(groups).toHaveLength(2);
    expect(groups.every((g) => g.clips.length === 0)).toBe(true);
  });
});

describe('deriveGroupedLibrary', () => {
  const recording = (name: string, startTime: number) =>
    item({ fileName: `${name}.mp4`, startTime, game: 'Overwatch' });
  const cut = (name: string, startTime: number) =>
    item({ fileName: `${name}.mp4`, contentType: 'clip', filePath: `clips/${name}.mp4`, startTime });

  it('pages over groups, so a recording is never split from its clips', () => {
    const items = [
      recording('a', NOW),
      cut('a-01', NOW),
      cut('a-02', NOW),
      recording('b', NOW - DAY),
      cut('b-01', NOW - DAY),
    ];

    // A page size of one would cut 'a' away from its clips if pagination were over items.
    const page = deriveGroupedLibrary(items, query({ pageSize: 1 }), NOW);
    expect(page.groups).toHaveLength(1);
    expect(page.groups[0].recording?.fileName).toBe('a.mp4');
    expect(page.groups[0].clips).toHaveLength(2);
    expect(page.pageCount).toBe(2);
    expect(page.groupCount).toBe(2);
    expect(page.matchCount).toBe(5);
  });

  it('follows the sort, rather than imposing its own order', () => {
    const items = [recording('new', NOW), recording('old', NOW - DAY)];
    const oldest = deriveGroupedLibrary(items, query({ sort: 'oldest' }), NOW);
    expect(oldest.groups.map((g) => g.recording?.fileName)).toEqual(['old.mp4', 'new.mp4']);
  });

  // An orphan heads its own group in place, rather than being swept into a trailing bucket where
  // the sort no longer reaches it.
  it('keeps a clip whose recording is gone in sort order', () => {
    const items = [recording('a', NOW), cut('gone-01', NOW - HOUR), recording('b', NOW - DAY)];
    const page = deriveGroupedLibrary(items, query(), NOW);
    expect(page.groups.map((g) => g.recording?.fileName ?? g.clips[0].fileName)).toEqual([
      'a.mp4',
      'gone-01.mp4',
      'b.mp4',
    ]);
  });

  it('clamps a page that no longer exists, like the flat view does', () => {
    const page = deriveGroupedLibrary([recording('a', NOW)], query({ page: 9 }), NOW);
    expect(page.page).toBe(1);
    expect(page.groups).toHaveLength(1);
  });
});
