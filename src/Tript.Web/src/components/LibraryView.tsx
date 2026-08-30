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
  type SelectOption,
} from '../components/ui/controls';
import { ContentCard } from './library/ContentCard';
import { RecordingGroup, type GroupActions } from './library/RecordingGroup';
import { ConfirmDeleteDialog, type DeleteConfirmation } from './library/ConfirmDeleteDialog';
import { LibraryPagination } from './library/LibraryPagination';
import { LibraryToolbar } from './library/LibraryToolbar';
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
  cascadableLinkedHighlights,
  DEFAULT_LIBRARY_QUERY,
  deriveGroupedLibrary,
  deriveLibrary,
  clampPage,
  groupByRecording,
  itemLabel,
  linkedAutomaticHighlights,
  NO_GAME,
  pageCountFor,
  UNKNOWN_GAME_LABEL,
  type ContentTypeFilter,
  type LibraryQuery,
} from './library/libraryModel';
import type { TrashController } from './trash/useTrash';
import { TrashList } from './trash/TrashList';
import { filterTrashEntries } from './trash/trashModel';
import { useWatchedGames } from './recorder/useWatchedGames';

/**
 * The type filters. Trash is one of them because deleted content is the same catalogue in a
 * different state, not another destination — and it carries its count, so a full trash is visible
 * without going there first.
 */
function typeFilters(trashCount: number): { value: ContentTypeFilter; label: string }[] {
  return [
    { value: 'all', label: 'All' },
    { value: 'sessions', label: 'Sessions' },
    { value: 'clips', label: 'Clips' },
    { value: 'trash', label: trashCount > 0 ? `Trash (${trashCount})` : 'Trash' },
  ];
}

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
   * The trash, which the library lists under its own type filter rather than a separate route:
   * deleted content is the same catalogue in a different state.
   */
  trash?: TrashController;
  /**
   * How long the backend keeps a trashed item, from the `trash` push. The delete confirmation quotes
   * it; the default only stands until the first push arrives.
   */
  retentionHours?: number;
  /** Current default used when a recording deletion dialog is opened. */
  deleteLinkedHighlightsByDefault?: boolean;
}
export function LibraryView({
  client,
  items,
  connectionState,
  contentLoaded = true,
  onOpen,
  nowSeconds,
  retentionHours = DEFAULT_RETENTION_HOURS,
  deleteLinkedHighlightsByDefault = false,
  trash,
}: LibraryViewProps) {
  const [query, setQuery] = useState<LibraryQuery>(DEFAULT_LIBRARY_QUERY);
  const [selectionMode, setSelectionMode] = useState(false);
  const [selected, setSelected] = useState<SelectionKey[]>([]);
  // The items a confirmed delete will act on, captured when the dialog opens.
  const [pendingDelete, setPendingDelete] = useState<ContentItem[] | null>(null);
  // Default to the real clock, read at render time. The date window only needs second-level accuracy,
  // so re-reading it per render is cheaper and simpler than keeping a ticking clock in state.
  const now = nowSeconds ?? Date.now() / 1000;
  const showingTrash = query.type === 'trash';
  const typeSegments = useMemo(() => typeFilters(trash?.entries.length ?? 0), [trash?.entries.length]);
  const view = useMemo(() => deriveLibrary(items, query, now), [items, query, now]);
  // Grouping is for the "everything" view. Asking for only recordings or only clips is asking for a
  // flat list of one kind, and grouping a clips-only view would put every clip under one headless
  // group — the flat grid with a heading on top of it.
  const grouped = query.type === 'all';
  const groupView = useMemo(
    () => (grouped ? deriveGroupedLibrary(items, query, now) : null),
    [grouped, items, query, now],
  );
  const watchedGames = useWatchedGames(client);
  // The two views paginate over different things — items, or groups — so the chrome reads its page
  // numbers from whichever one is rendering rather than from `view` unconditionally.
  const page = groupView?.page ?? view.page;
  const pageCount = groupView?.pageCount ?? view.pageCount;
  const games = useMemo(
    () => availableGames(showingTrash && trash ? trash.entries.map((entry) => ({ ...entry, filePath: entry.fileName })) : items),
    [items, showingTrash, trash],
  );
  const trashFiltered = query.game !== ANY_GAME || query.range !== 'any' || query.search.trim().length > 0;
  const trashEntries = useMemo(
    () => (trash ? filterTrashEntries(trash.entries, query, now) : []),
    [trash, query, now],
  );

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
    setQuery((previous) => ({
      ...DEFAULT_LIBRARY_QUERY,
      type: showingTrash ? 'trash' : 'all',
      sort: previous.sort,
      pageSize: previous.pageSize,
    }));
  }, [showingTrash]);

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
    (permanent: boolean, deleteLinkedHighlights: boolean) => {
      const targets = pendingDelete ?? [];
      const parameters = targets.map(
        (item): DeleteContentParameters => ({
          contentType: item.contentType,
          fileName: item.filePath,
          ...(item.contentType === 'recording' && deleteLinkedHighlights
            ? { deleteLinkedHighlights: true }
            : {}),
        }),
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
    // Count what the button really moves, not what the dialog named. A missing-video placeholder
    // session names itself but deletes nothing on its own; checked cascades add the non-favourited
    // highlights the listing would otherwise hide.
    const physicalTargets = pendingDelete.reduce(
      (count, item) => count + (item.contentType === 'recording' && item.videoMissing === true ? 0 : 1),
      0,
    );
    const cascadable = pendingDelete.reduce(
      (count, item) =>
        count + (item.contentType === 'recording' ? cascadableLinkedHighlights(item, items).length : 0),
      0,
    );
    return {
      title: names.length === 1 ? `Delete "${names[0]}"?` : `Delete ${names.length} items?`,
      names,
      confirmLabel: names.length === 1 ? 'Move to trash' : `Move ${names.length} to trash`,
      affectedCount: physicalTargets,
      ...(pendingDelete.some((item) => item.contentType === 'recording')
        ? {
            cascadeCount: cascadable,
            checkbox: {
              label: 'Delete linked highlights (favourited highlights are kept)',
              defaultChecked: deleteLinkedHighlightsByDefault,
            },
          }
        : {}),
      retentionHours,
    };
  }, [deleteLinkedHighlightsByDefault, items, pendingDelete, retentionHours]);

  const groupActions: GroupActions = useMemo(
    () => ({
      onOpen: (item: ContentItem) => onOpen?.(item, groupView?.resultItems ?? []),
      onDelete: requestDelete,
      onToggleFavorite: toggleFavorite,
      selectable: selectionMode,
      isSelected: (item: ContentItem) => selection.includes(selectionKey(item)),
      onToggleSelected: toggleItem,
    }),
    [onOpen, groupView, requestDelete, toggleFavorite, selectionMode, selection, toggleItem],
  );

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

  const allGroups = groupView && !groupView.filtered ? groupByRecording(groupView.resultItems) : [];
  // The flat grids (the Latest shelf and the ungrouped view) show a recording next to its clips like
  // groupings do, so their cards need the same clips/highlights counts the recording groups carry.
  // Built from the whole list so the badge is stable whether it sits in a group or a flat grid.
  const recordingCounts = useMemo(() => {
    const counts = new Map<string, { clips: number; highlights: number }>();
    for (const group of groupByRecording(items)) {
      if (!group.recording) continue;
      counts.set(group.recording.filePath, {
        clips: group.clips.length,
        highlights: group.clips.filter((clip) => clip.automated).length,
      });
    }
    return counts;
  }, [items]);
  const recentGroups = groupView && !groupView.filtered
    ? allGroups.filter((group) => group.recording !== null).slice(0, 3)
    : [];
  const recentRecordingPaths = new Set(recentGroups.map((group) => group.recording!.filePath));
  const allLatestItems = groupView
    ? groupView.resultItems.filter((item) => !recentRecordingPaths.has(item.filePath))
    : [];
  const latestPageCount = groupView && !groupView.filtered
    ? pageCountFor(allLatestItems.length, query.pageSize)
    : pageCount;
  const latestPage = groupView && !groupView.filtered
    ? clampPage(query.page, latestPageCount)
    : page;
  const latestItems = groupView && !groupView.filtered
    ? allLatestItems.slice((latestPage - 1) * query.pageSize, latestPage * query.pageSize)
    : allLatestItems;
  const showPagination = groupView ? latestPageCount > 1 : pageCount > 1;

  return (
    <section className="library-view">
      {/* A plain div, not a <header>: the shell's recorder bar is the page's banner landmark, and a
          second header element muddies that (some accessibility mappings promote any <header> to
          banner) for a row that is only a count. */}

      {/* Deleted content is the same catalogue in a different state, so its useful filters remain
          available while the Trash type is selected. */}
      {showingTrash && trash ? (
        <>
          <LibraryToolbar
            query={query}
            typeSegments={typeSegments}
            gameOptions={gameOptions}
            dateOptions={DATE_OPTIONS}
            sortOptions={SORT_OPTIONS}
            showingTrash={showingTrash}
            filtered={trashFiltered}
            onQueryChange={updateQuery}
            onClearFilters={clearFilters}
          />
          <TrashList
            trash={trash}
            nowSeconds={nowSeconds}
            entries={trashEntries}
            filtered={trashFiltered}
            onClearFilters={clearFilters}
          />
        </>
      ) : (
      <>
      <LibraryToolbar
        query={query}
        typeSegments={typeSegments}
        gameOptions={gameOptions}
        dateOptions={DATE_OPTIONS}
        sortOptions={SORT_OPTIONS}
        showingTrash={showingTrash}
        filtered={view.filtered}
        onQueryChange={updateQuery}
        onClearFilters={clearFilters}
      />

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
                  {/* A grouped page holds a variable number of items — pagination is over groups so
                      that a session is never split from its clips — so it counts sessions. */}
                  {groupView
                    ? `${groupView.groupCount} session${groupView.groupCount === 1 ? '' : 's'} · ${groupView.matchCount} item${groupView.matchCount === 1 ? '' : 's'}`
                    : `Showing ${view.firstIndex}–${view.lastIndex} of ${view.matchCount}`}
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
        // No content at all. Worded as an expectation rather than an error: a fresh install and a
        // backend that is not answering look identical here, and the connection badge in the recorder
        // bar is what tells those apart.
        //
        // It must NOT ask the user to add a game. Detection is automatic and the game list ships
        // seeded, so the only true instruction is "play". The watched names come from the live list,
        // never a hard-coded "Overwatch", so the line stays true as more games are supported — and it
        // is the same phrasing the idle recorder status uses, so the two agree.
        <div data-testid="library-empty">
          <EmptyState
            title="Nothing recorded yet"
            description={
              watchedGames.length > 0
                ? `Tript is watching for ${watchedGames.join(', ')} — start playing and it records on its own.`
                : 'Tript records on its own once it recognises a game on screen. Start playing.'
            }
            action={
              <Button variant="primary" onClick={() => client.send('StartRecording')}>
                Record now
              </Button>
            }
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
       ) : groupView ? (
         <div className="library-groups" data-testid="library-groups">
           {recentGroups.length > 0 && latestPage === 1 && (
             <section className="library-recent" data-testid="library-recent" aria-labelledby="library-recent-title">
              <div className="library-section-heading">
                <div>
                  <h2 id="library-recent-title">Recent sessions</h2>
                </div>
                 <span className="library-section-kicker">{recentGroups.length} latest</span>
               </div>
               <div className="library-recent-grid">
                 {recentGroups.map((group) => (
                  <RecordingGroup
                    key={group.recording?.filePath ?? 'orphaned-clips'}
                    group={group}
                    variant="recent"
                    actions={groupActions}
                  />
                ))}
              </div>
             </section>
           )}
           {latestItems.length > 0 && (
             <section className="library-latest" data-testid="library-latest">
               <div className="library-section-heading">
                 <h2>Latest</h2>
                 <span className="library-section-kicker">{latestItems.length} items</span>
               </div>
                <ul className="library-grid" data-testid="library-latest-grid">
                 {latestItems.map((item) => (
                   <li key={selectionKey(item)}>
                       <ContentCard
                         item={item}
                         clipsCount={recordingCounts.get(item.filePath)?.clips ?? 0}
                         highlightsCount={recordingCounts.get(item.filePath)?.highlights ?? 0}
                         previewHighlights={item.videoMissing || item.recording ? linkedAutomaticHighlights(item, items) : undefined}
                        onOpen={(item) => onOpen?.(item, groupView.resultItems)}
                       onDelete={requestDelete}
                       onToggleFavorite={toggleFavorite}
                       selectable={selectionMode}
                       selected={selection.includes(selectionKey(item))}
                       onToggleSelected={toggleItem}
                     />
                   </li>
                 ))}
               </ul>
             </section>
           )}
         </div>
      ) : (
        <ul className="library-grid" data-testid="library-grid">
          {view.items.map((item) => (
            <li key={selectionKey(item)}>
                <ContentCard
                  item={item}
                  clipsCount={recordingCounts.get(item.filePath)?.clips ?? 0}
                  highlightsCount={recordingCounts.get(item.filePath)?.highlights ?? 0}
                  previewHighlights={item.videoMissing || item.recording ? linkedAutomaticHighlights(item, items) : undefined}
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

      {showPagination && (
        <LibraryPagination
          page={groupView && !groupView.filtered ? latestPage : page}
          pageCount={latestPageCount}
          onPageChange={goToPage}
        />
      )}

      </>
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
