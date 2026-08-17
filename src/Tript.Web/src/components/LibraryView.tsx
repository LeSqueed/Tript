// SPDX-License-Identifier: GPL-2.0-or-later
//
// The library — ONE grid over everything the backend has: sessions and clips together.
//
// There is no separate clips page any more. "Is it a clip" turned out to be a filter dimension like
// the game and the date are, and splitting it across two tabs meant the same list, the same card, the
// same sort and the same pagination existed twice while the user still could not ask the obvious
// question ("everything from this game, newest first"). One grid with a type filter answers it.
//
// Items come from the shared IPC session source, owned by the App shell (so there is exactly one
// `ListContent` per connection, however many views read it), and the list is live: new recordings
// appear after StopRecording, new clips when the backend finishes them, deletes and renames are
// reflected on the next `content` push.
//
// WHAT THIS COMPONENT OWNS AND WHAT IT DOES NOT. It owns the query — the user's filters, sort and page
// — as plain state, and nothing else: every derived thing (which items are on the page, how many
// matched, which page actually exists) comes from `deriveLibrary` in library/libraryModel.ts, which is
// pure and unit-tested without a DOM. The state living HERE, and the component staying mounted while
// the player overlay is up, is what makes "close the player and you are back where you were" true
// without a single line of save/restore code.
//
// PAGINATION AND FILTERS, THE ONE RULE. A filter change resets to page 1 (`updateQuery` below does it
// for every filter in one place, so a new filter cannot forget), and `deriveLibrary` clamps whatever
// page it is given into the range that exists. Both halves are needed: the reset is what the user
// expects from an interaction, the clamp is what covers a page going stale for a reason nobody
// interacted with — a `content` push that removed items while the user sat on the last page.
//
// Nothing here waits on the optional wire fields (game, duration, size, thumbnail). Each has a
// documented fallback in the model or on the card, because a missing field must never be able to hide
// a recording the user made.

import { useCallback, useMemo, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { ContentItem } from '../ipc/protocol';
import { Field, SelectField, TextField, type SelectOption } from '../settings/form';
import { ContentCard } from './library/ContentCard';
import {
  ANY_GAME,
  availableGames,
  DEFAULT_LIBRARY_QUERY,
  deriveLibrary,
  NO_GAME,
  UNKNOWN_GAME_LABEL,
  type ContentTypeFilter,
  type DateRangeFilter,
  type LibraryQuery,
  type LibrarySort,
} from './library/libraryModel';

const TYPE_FILTERS: { value: ContentTypeFilter; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'sessions', label: 'Sessions' },
  { value: 'clips', label: 'Clips' },
];

const DATE_OPTIONS: SelectOption[] = [
  { value: 'any', label: 'Any time' },
  { value: 'day', label: 'Last 24 hours' },
  { value: 'week', label: 'Last 7 days' },
  { value: 'month', label: 'Last 30 days' },
  { value: 'year', label: 'Last year' },
];

const SORT_OPTIONS: SelectOption[] = [
  { value: 'newest', label: 'Newest first' },
  { value: 'oldest', label: 'Oldest first' },
  { value: 'game', label: 'Game (A–Z)' },
];

export interface LibraryViewProps {
  client: IpcClient;
  /** Everything the backend has, in its own order (newest-first), reactive via the shell's source. */
  items: ContentItem[];
  /** The shell's player seam: called with the item the user opened. */
  onOpen?: (item: ContentItem) => void;
  /**
   * The clock the date filter measures its trailing window against, in epoch seconds. Injectable so
   * a test can put "now" somewhere fixed relative to its fixtures instead of racing the real clock.
   */
  nowSeconds?: number;
}

export function LibraryView({ client, items, onOpen, nowSeconds }: LibraryViewProps) {
  const [query, setQuery] = useState<LibraryQuery>(DEFAULT_LIBRARY_QUERY);
  // Default to the real clock, read at render time. The date window only needs second-level accuracy,
  // so re-reading it per render is cheaper and simpler than keeping a ticking clock in state.
  const now = nowSeconds ?? Date.now() / 1000;
  const view = useMemo(() => deriveLibrary(items, query, now), [items, query, now]);
  const games = useMemo(() => availableGames(items), [items]);

  /**
   * Change a filter or the sort. Page goes back to 1 for BOTH: a filter changes which items exist and
   * a sort changes which ones are near the front, so in either case "page 4" no longer means what the
   * user was looking at. Doing it here, rather than at each control, is what stops a future filter
   * from being added without the reset.
   */
  const updateQuery = useCallback((patch: Partial<LibraryQuery>) => {
    setQuery((previous) => ({ ...previous, ...patch, page: 1 }));
  }, []);

  const goToPage = useCallback((page: number) => {
    setQuery((previous) => ({ ...previous, page }));
  }, []);

  const clearFilters = useCallback(() => {
    // The sort survives: it is not a filter, and it is not what emptied the grid.
    setQuery((previous) => ({ ...DEFAULT_LIBRARY_QUERY, sort: previous.sort, pageSize: previous.pageSize }));
  }, []);

  const gameOptions: SelectOption[] = useMemo(() => {
    const options: SelectOption[] = [
      { value: ANY_GAME, label: 'All games' },
      ...games.names.map((name) => ({ value: name, label: name })),
      // Only offered when something actually lacks a game — an option that can only ever match
      // nothing is worse than no option.
      ...(games.hasUnknown ? [{ value: NO_GAME, label: UNKNOWN_GAME_LABEL }] : []),
    ];
    // The selected game can leave the list under the user: a `content` push deletes its last item and
    // the option derived from the items goes with it. It is kept while it is still selected, because a
    // `<select>` whose value matches none of its options renders BLANK — which reads as a broken
    // control, when what actually happened is a filter that now matches nothing. The grid's
    // filtered-empty state is already saying so and offering the way out.
    if (!options.some((option) => option.value === query.game)) {
      options.push({
        value: query.game,
        label: query.game === NO_GAME ? UNKNOWN_GAME_LABEL : query.game,
      });
    }
    return options;
  }, [games, query.game]);

  return (
    <section className="library-view">
      {/* A plain div, not a <header>: the shell's recorder bar is the page's banner landmark, and a
          second header element muddies that (some accessibility mappings promote any <header> to
          banner) for a row that is only a heading and a count. */}
      <div className="library-header">
        <h2>Library</h2>
        {view.matchCount > 0 && (
          <span className="library-range muted small" data-testid="library-range">
            Showing {view.firstIndex}–{view.lastIndex} of {view.matchCount}
            {/* How much is being hidden is worth stating: it is the difference between "I have 3
                recordings" and "my filter is hiding 22 of them". */}
            {view.filtered && view.totalCount !== view.matchCount ? ` · ${view.totalCount} total` : ''}
          </span>
        )}
      </div>

      <div className="library-toolbar">
        <div className="library-types" role="group" aria-label="Content type">
          {TYPE_FILTERS.map((filter) => (
            <button
              key={filter.value}
              type="button"
              className={query.type === filter.value ? 'library-type active' : 'library-type'}
              aria-pressed={query.type === filter.value}
              onClick={() => updateQuery({ type: filter.value })}
            >
              {filter.label}
            </button>
          ))}
        </div>

        <div className="library-filters">
          <Field label="Game">
            <SelectField
              value={query.game}
              onChange={(value) => updateQuery({ game: value })}
              options={gameOptions}
            />
          </Field>
          <Field label="Date">
            <SelectField
              value={query.range}
              onChange={(value) => updateQuery({ range: value as DateRangeFilter })}
              options={DATE_OPTIONS}
            />
          </Field>
          <Field label="Sort">
            <SelectField
              value={query.sort}
              onChange={(value) => updateQuery({ sort: value as LibrarySort })}
              options={SORT_OPTIONS}
            />
          </Field>
          <Field label="Search">
            <TextField
              value={query.search}
              onChange={(value) => updateQuery({ search: value })}
              placeholder="Title or game"
            />
          </Field>
          {view.filtered && (
            <button type="button" className="btn ghost library-clear" onClick={clearFilters}>
              Clear filters
            </button>
          )}
        </div>
      </div>

      {view.totalCount === 0 ? (
        // No content at all. Deliberately worded as an expectation rather than as an error: a fresh
        // install and a backend that is not answering look identical here, and the connection badge in
        // the recorder bar is what tells those apart.
        <p className="library-empty muted" data-testid="library-empty">
          Your recordings and clips will appear here.
        </p>
      ) : view.matchCount === 0 ? (
        // Content exists but the filters hide all of it. This is the state that MUST be distinguishable
        // from the one above: without the distinction, a too-narrow filter is indistinguishable from a
        // broken backend, and the user's next move (clear the filters) is invisible.
        <div className="library-empty" data-testid="library-empty-filtered">
          <p className="muted">
            None of your {view.totalCount} item{view.totalCount === 1 ? '' : 's'} matches these filters.
          </p>
          <button type="button" className="btn" onClick={clearFilters}>
            Clear filters
          </button>
        </div>
      ) : (
        <ul className="library-grid" data-testid="library-grid">
          {view.items.map((item) => (
            <li key={`${item.contentType}:${item.filePath}`}>
              <ContentCard item={item} onOpen={onOpen} />
            </li>
          ))}
        </ul>
      )}

      {view.pageCount > 1 && (
        <nav className="library-pagination" aria-label="Library pages">
          <button
            type="button"
            className="btn ghost"
            onClick={() => goToPage(view.page - 1)}
            disabled={view.page <= 1}
            aria-label="Previous page"
          >
            ← Prev
          </button>
          <span className="library-page-indicator" data-testid="library-page">
            Page {view.page} of {view.pageCount}
          </span>
          <button
            type="button"
            className="btn ghost"
            onClick={() => goToPage(view.page + 1)}
            disabled={view.page >= view.pageCount}
            aria-label="Next page"
          >
            Next →
          </button>
        </nav>
      )}

      <LiveIpcProbe client={client} />
    </section>
  );
}

/**
 * Live IPC round-trip proof: sends commands on the control socket and the state push appears in the
 * recorder bar. Collapsed into a `<details>` so the grid owns the page — but kept, and kept here,
 * because these are still the only Start/Stop recording affordances in the shell: a cleaner library
 * must not be a library you can no longer record from. Fails gracefully when the backend is not
 * running (the connection badge shows it).
 */
function LiveIpcProbe({ client }: { client: IpcClient }) {
  return (
    <details className="live-probe">
      <summary>Connection probe</summary>
      <div className="probe-actions">
        <button type="button" className="btn" onClick={() => client.send('StartRecording')}>
          Start recording
        </button>
        <button type="button" className="btn" onClick={() => client.send('StopRecording')}>
          Stop recording
        </button>
        <button type="button" className="btn" onClick={() => client.send('CheckForUpdates')}>
          Check for updates
        </button>
      </div>
      <p className="muted small">
        If the backend is running, these send commands on the control socket and the state push appears
        in the recorder bar.
      </p>
    </details>
  );
}
