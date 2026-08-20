// SPDX-License-Identifier: GPL-2.0-or-later
//
// The library — ONE grid over everything the backend has: sessions and clips together. There is no
// separate clips page any more.

import { useCallback, useMemo, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { ConnectionState } from '../ipc/websocketClient';
import type { ContentItem, DeleteContentParameters } from '../ipc/protocol';
import {
  Button,
  Field,
  SegmentedControl,
  SelectField,
  TextField,
  Toggle,
  type SelectOption,
} from '../components/ui/controls';
import { ContentCard } from './library/ContentCard';
import { ConfirmDeleteDialog, type DeleteConfirmation } from './library/ConfirmDeleteDialog';
import {
  addSelection,
  allSelected,
  pruneSelection,
  removeSelection,
  selectedItems,
  selectionKey,
  toggleSelection,
  type SelectionKey,
} from './library/selectionModel';
import { DEFAULT_RETENTION_HOURS } from './trash/trashModel';
import { EmptyState } from './ui/Ui';
import {
  ANY_GAME,
  availableGames,
  DEFAULT_LIBRARY_QUERY,
  deriveLibrary,
  itemLabel,
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
  connectionState?: ConnectionState;
  contentLoaded?: boolean;
  /** The shell's player seam: called with the item the user opened. */
  onOpen?: (item: ContentItem, resultItems: ContentItem[]) => void;
  /**
   * The clock the date filter measures its trailing window against, in epoch seconds. Injectable so
   * a test can put "now" somewhere fixed relative to its fixtures instead of racing the real clock.
   */
  nowSeconds?: number;
  /**
   * How long the backend keeps a trashed item, from the `trash` push. The delete confirmation quotes
   * it; the default only stands until the first push arrives.
   */
  retentionHours?: number;
}
export function LibraryView({
  client,
  items,
  connectionState,
  contentLoaded = true,
  onOpen,
  nowSeconds,
  retentionHours = DEFAULT_RETENTION_HOURS,
}: LibraryViewProps) {
  const [query, setQuery] = useState<LibraryQuery>(DEFAULT_LIBRARY_QUERY);
  const [selectionMode, setSelectionMode] = useState(false);
  const [selected, setSelected] = useState<SelectionKey[]>([]);
  // The items a confirmed delete will act on, captured when the dialog opens.
  const [pendingDelete, setPendingDelete] = useState<ContentItem[] | null>(null);
  // Default to the real clock, read at render time. The date window only needs second-level accuracy,
  // so re-reading it per render is cheaper and simpler than keeping a ticking clock in state.
  const now = nowSeconds ?? Date.now() / 1000;
  const view = useMemo(() => deriveLibrary(items, query, now), [items, query, now]);
  const games = useMemo(() => availableGames(items), [items]);

  /**
   * Change a filter or the sort. Page goes back to 1 for BOTH: a filter changes which items exist
   * and a sort changes which ones are near the front, so in either case "page 4" no longer means
   * what the user was looking at.
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

  // Applied on the way OUT rather than stored pruned: a `content` push replaces the item array, and
  // the selection must not name something that has since left the list even for one render.
  const liveKeys = useMemo(() => new Set(items.map(selectionKey)), [items]);
  const selection = useMemo(() => pruneSelection(selected, liveKeys), [selected, liveKeys]);
  const pageKeys = useMemo(() => view.items.map(selectionKey), [view.items]);
  const pageAllSelected = allSelected(pageKeys, selection);
  const selectedCount = selection.length;

  const toggleSelectionMode = useCallback(() => {
    setSelectionMode((previous) => {
      if (previous) {
        setSelected([]);
      }
      return !previous;
    });
  }, []);

  const toggleItem = useCallback((item: ContentItem) => {
    setSelected((previous) => toggleSelection(previous, selectionKey(item)));
  }, []);

  const toggleAllOnPage = useCallback(() => {
    setSelected((previous) =>
      pageAllSelected ? removeSelection(previous, pageKeys) : addSelection(previous, pageKeys),
    );
  }, [pageAllSelected, pageKeys]);

  const clearSelection = useCallback(() => setSelected([]), []);

  const requestDelete = useCallback((item: ContentItem) => setPendingDelete([item]), []);

  const requestBulkDelete = useCallback(() => {
    const targets = selectedItems(items, selection);
    if (targets.length > 0) {
      setPendingDelete(targets);
    }
  }, [items, selection]);

  const toggleFavorite = useCallback((item: ContentItem) => {
    client.send('ToggleFavorite', {
      contentType: item.contentType,
      filePath: item.filePath,
      favorite: item.favorite !== true,
    });
  }, [client]);

  const cancelDelete = useCallback(() => setPendingDelete(null), []);

  const confirmDelete = useCallback(
    (permanent: boolean) => {
      const targets = pendingDelete ?? [];
      const parameters = targets.map(
        (item): DeleteContentParameters => ({ contentType: item.contentType, fileName: item.filePath }),
      );
      // `permanent` is omitted rather than sent false — the contract reads omitted/false as "trash",
      // and the quieter frame is the one that cannot be misread.
      const flag = permanent ? { permanent: true } : {};
      if (parameters.length === 1) {
        client.send('DeleteContent', { ...parameters[0], ...flag });
      } else if (parameters.length > 1) {
        client.send('DeleteMultipleContent', { items: parameters, ...flag });
      }
      // Drop just what was sent. The rest of the selection is still valid, and the `content` push
      // that follows is what actually removes the cards.
      setSelected((previous) => removeSelection(previous, targets.map(selectionKey)));
      setPendingDelete(null);
    },
    [client, pendingDelete],
  );

  const confirmation: DeleteConfirmation | null = useMemo(() => {
    if (pendingDelete === null || pendingDelete.length === 0) {
      return null;
    }
    const names = pendingDelete.map(itemLabel);
    return {
      title: names.length === 1 ? `Delete "${names[0]}"?` : `Delete ${names.length} items?`,
      names,
      confirmLabel: names.length === 1 ? 'Move to trash' : `Move ${names.length} to trash`,
      retentionHours,
    };
  }, [pendingDelete, retentionHours]);

  const gameOptions: SelectOption[] = useMemo(() => {
    const options: SelectOption[] = [
      { value: ANY_GAME, label: 'All games' },
      ...games.names.map((name) => ({ value: name, label: name })),
      // Only offered when something actually lacks a game — an option that can only ever match
      // nothing is worse than no option.
      ...(games.hasUnknown ? [{ value: NO_GAME, label: UNKNOWN_GAME_LABEL }] : []),
    ];
    // The selected game can leave the list under the user: a `content` push deletes its last item
    // and the option derived from the items goes with it. It is kept while it is still selected,
    // because a `<select>` whose value matches none of its options renders BLANK — which reads as a
    // broken control, when what actually happened is a filter that now matches nothing.
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
          banner) for a row that is only a count. */}

      <div className="library-toolbar">
        {/* Content type is one-of-N. Favourites is an independent boolean and lives with the other
            filters — inside this group it read, and was announced, as a fourth exclusive type. */}
        <SegmentedControl
          label="Content type"
          value={query.type}
          segments={TYPE_FILTERS}
          onChange={(value) => updateQuery({ type: value })}
        />

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

          <Toggle
            checked={query.favoriteOnly}
            onChange={(checked) => updateQuery({ favoriteOnly: checked })}
            label="Favourites only"
          />
          {view.filtered && (
            <Button variant="ghost" className="library-clear"  onClick={clearFilters}>
              Clear filters
            </Button>
          )}
        </div>
      </div>

      {view.totalCount > 0 && (
        <div className="library-selection" data-testid="library-selection">
          {selectionMode ? (
            <>
              {/* A live region: the count is the only feedback a checkbox click gives, and a user who
                  cannot see the highlighted cards has nothing else to go on. */}
              <span className="library-selection-count" data-testid="library-selection-count" aria-live="polite">
                {selectedCount} selected
              </span>
              <Button variant="ghost"  onClick={toggleAllOnPage} disabled={pageKeys.length === 0}>
                {pageAllSelected ? 'Deselect page' : 'Select page'}
              </Button>
              <Button variant="ghost"  onClick={clearSelection} disabled={selectedCount === 0}>
                Clear selection
              </Button>
              <Button variant="danger"
                
                onClick={requestBulkDelete}
                disabled={selectedCount === 0}>
                Delete {selectedCount > 0 ? selectedCount : ''}
              </Button>
              <Button variant="ghost" className="library-selection-done"  onClick={toggleSelectionMode}>
                Done
              </Button>
            </>
          ) : (
            <>
              <Button variant="ghost" onClick={toggleSelectionMode}>
                Select
              </Button>
              {view.matchCount > 0 && (
                <span className="library-range muted small" data-testid="library-range">
                  Showing {view.firstIndex}–{view.lastIndex} of {view.matchCount}
                  {view.filtered && view.totalCount !== view.matchCount
                    ? ` · ${view.totalCount} total`
                    : ''}
                </span>
              )}
            </>
          )}
        </div>
      )}

      {view.totalCount === 0 && connectionState === 'disconnected' ? (
        <div data-testid="library-unavailable">
          <EmptyState
            title="Library unavailable"
            description="Tript cannot reach the capture host right now. Reconnect to load your archive."
          />
        </div>
      ) : view.totalCount === 0 && (!contentLoaded || connectionState === 'connecting') ? (
        <div data-testid="library-loading">
          <EmptyState
            title="Loading your archive"
            description="Tript is asking the capture host for your recordings and clips."
          />
        </div>
      ) : view.totalCount === 0 ? (
        // No content at all. Deliberately worded as an expectation rather than as an error: a fresh
        // install and a backend that is not answering look identical here, and the connection badge in
        // the recorder bar is what tells those apart.
        <div data-testid="library-empty">
          <EmptyState
            title="Your archive starts here"
            description="Your recordings and clips will appear here. Record a session and Tript will keep the source material ready for review."
          />
        </div>
      ) : view.matchCount === 0 ? (
        // Content exists but the filters hide all of it. This is the state that MUST be distinguishable
        // from the one above: without the distinction, a too-narrow filter is indistinguishable from a
        // broken backend, and the user's next move (clear the filters) is invisible.
        <div data-testid="library-empty-filtered">
          <EmptyState
            title="Nothing fits this view"
            description={`None of your ${view.totalCount} item${view.totalCount === 1 ? '' : 's'} matches these filters.`}
            action={
              <Button variant="primary"  onClick={clearFilters}>
                Clear filters
              </Button>
            }
          />
        </div>
      ) : (
        <ul className="library-grid" data-testid="library-grid">
          {view.items.map((item) => (
            <li key={selectionKey(item)}>
              <ContentCard
                item={item}
                onOpen={(item) => onOpen?.(item, view.resultItems)}
                onDelete={requestDelete}
                onToggleFavorite={toggleFavorite}
                selectable={selectionMode}
                selected={selection.includes(selectionKey(item))}
                onToggleSelected={toggleItem}
              />
            </li>
          ))}
        </ul>
      )}

      {view.pageCount > 1 && (
        <nav className="library-pagination" aria-label="Library pages">
          <Button variant="ghost"
            
            onClick={() => goToPage(view.page - 1)}
            disabled={view.page <= 1}
            aria-label="Previous page"
          >
            ← Prev
          </Button>
          <span className="library-page-indicator" data-testid="library-page">
            Page {view.page} of {view.pageCount}
          </span>
          <Button variant="ghost"
            
            onClick={() => goToPage(view.page + 1)}
            disabled={view.page >= view.pageCount}
            aria-label="Next page"
          >
            Next →
          </Button>
        </nav>
      )}

      {confirmation && (
        <ConfirmDeleteDialog
          confirmation={confirmation}
          onCancel={cancelDelete}
          onConfirm={confirmDelete}
        />
      )}
    </section>
  );
}
