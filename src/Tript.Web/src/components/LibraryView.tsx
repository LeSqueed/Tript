// SPDX-License-Identifier: GPL-2.0-or-later

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
import { ConfirmDeleteDialog, makeDeleteConfirmation, type DeleteConfirmation } from './library/ConfirmDeleteDialog';
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
import { ActionBar, EmptyState, FilterMismatchEmptyState } from './ui/Ui';
import {
  ANY_GAME,
  availableGames,
  cascadableLinkedHighlights,
  DATE_OPTIONS,
  DEFAULT_LIBRARY_QUERY,
  deriveGroupedLibrary,
  deriveLibrary,
  clampPage,
  groupByRecording,
  itemLabel,
  lacksMainVideo,
  NO_GAME,
  pageCountFor,
  recordingChildCounts,
  sessionPreviewHighlights,
  SORT_OPTIONS,
  UNKNOWN_GAME_LABEL,
  type ContentTypeFilter,
  type LibraryQuery,
} from './library/libraryModel';
import type { TrashController } from './trash/useTrash';
import { TrashList } from './trash/TrashList';
import { filterTrashEntries } from './trash/trashModel';

function typeFilters(trashCount: number): { value: ContentTypeFilter; label: string }[] {
  return [
    { value: 'all', label: 'All' },
    { value: 'sessions', label: 'Sessions' },
    { value: 'clips', label: 'Clips' },
    { value: 'highlights', label: 'Highlights' },
    { value: 'trash', label: trashCount > 0 ? `Trash (${trashCount})` : 'Trash' },
  ];
}

export interface LibraryViewProps {
  client: IpcClient;
  items: ContentItem[];
  thumbnailLoadingActive?: boolean;
  connectionState?: ConnectionState;
  contentLoaded?: boolean;
  onOpen?: (item: ContentItem, resultItems: ContentItem[], origin: 'library' | 'session') => void;
  nowSeconds?: number;
  trash?: TrashController;
  retentionHours?: number;
  deleteLinkedHighlightsByDefault?: boolean;
}
export function LibraryView({
  client,
  items,
  thumbnailLoadingActive = true,
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
  const [pendingDelete, setPendingDelete] = useState<ContentItem[] | null>(null);
  const now = nowSeconds ?? Date.now() / 1000;
  const showingTrash = query.type === 'trash';
  const typeSegments = useMemo(() => typeFilters(trash?.entries.length ?? 0), [trash?.entries.length]);
  const view = useMemo(() => deriveLibrary(items, query, now), [items, query, now]);
  const grouped = query.type === 'all';
  const groupView = useMemo(
    () => (grouped ? deriveGroupedLibrary(items, query, now) : null),
    [grouped, items, query, now],
  );
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

  const updateQuery = useCallback((patch: Partial<LibraryQuery>) => {
    setQuery((previous) => ({ ...previous, ...patch, page: 1 }));
  }, []);

  const goToPage = useCallback((page: number) => {
    setQuery((previous) => ({ ...previous, page }));
  }, []);

  const clearFilters = useCallback(() => {
    setQuery((previous) => ({
      ...DEFAULT_LIBRARY_QUERY,
      type: showingTrash ? 'trash' : 'all',
      sort: previous.sort,
      pageSize: previous.pageSize,
    }));
  }, [showingTrash]);

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
    if (item.highlightsOnly === true) {
      return;
    }
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
      const flag = permanent ? { permanent: true } : {};
      if (parameters.length === 1) {
        client.send('DeleteContent', { ...parameters[0], ...flag });
      } else if (parameters.length > 1) {
        client.send('DeleteMultipleContent', { items: parameters, ...flag });
      }
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
    const physicalTargets = pendingDelete.reduce(
      (count, item) => count + (
        item.contentType === 'recording' && (item.videoMissing === true || item.highlightsOnly === true) ? 0 : 1
      ),
      0,
    );
    const cascadable = pendingDelete.reduce(
      (count, item) =>
        count + (item.contentType === 'recording' ? cascadableLinkedHighlights(item, items).length : 0),
      0,
    );
    return makeDeleteConfirmation({
      names,
      retentionHours,
      affectedCount: physicalTargets,
      hasCascade: pendingDelete.some((item) => item.contentType === 'recording'),
      cascadeCount: cascadable,
      deleteLinkedHighlightsDefault: deleteLinkedHighlightsByDefault,
    });
  }, [deleteLinkedHighlightsByDefault, items, pendingDelete, retentionHours]);

  const groupActions: GroupActions = useMemo(
    () => ({
      onOpen: (item: ContentItem) => onOpen?.(item, groupView?.resultItems ?? [], 'session'),
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
      ...(games.hasUnknown ? [{ value: NO_GAME, label: UNKNOWN_GAME_LABEL }] : []),
    ];
    if (!options.some((option) => option.value === query.game)) {
      options.push({
        value: query.game,
        label: query.game === NO_GAME ? UNKNOWN_GAME_LABEL : query.game,
      });
    }
    return options;
  }, [games, query.game]);

  const allGroups = groupView && !groupView.filtered ? groupByRecording(groupView.resultItems) : [];
  const recordingCounts = useMemo(() => recordingChildCounts(items), [items]);
  const recentGroups = groupView && !groupView.filtered
    ? allGroups.filter((group) => group.recording !== null).slice(0, 3)
    : [];
  const recentRecordingPaths = new Set(recentGroups.map((group) => group.recording!.filePath));
  const allLatestItems = groupView
    ? groupView.resultItems.filter(
        (item) => !recentRecordingPaths.has(item.filePath) && !lacksMainVideo(item),
      )
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
      {}

      {}
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
        selectionMode ? (
          <ActionBar
            data-testid="library-selection"
            leading={
              <span className="action-bar-count" data-testid="library-selection-count" aria-live="polite">
                {selectedCount} selected
              </span>
            }
            trailing={<Button variant="ghost" onClick={toggleSelectionMode}>Done</Button>}
          >
            <Button variant="ghost" onClick={toggleAllOnPage} disabled={pageKeys.length === 0}>
              {pageAllSelected ? 'Deselect page' : 'Select page'}
            </Button>
            <Button variant="ghost" onClick={clearSelection} disabled={selectedCount === 0}>
              Clear selection
            </Button>
            <Button variant="danger" onClick={requestBulkDelete} disabled={selectedCount === 0}>
              Delete {selectedCount > 0 ? selectedCount : ''}
            </Button>
          </ActionBar>
        ) : (
          <div className="library-selection" data-testid="library-selection">
            <Button variant="ghost" onClick={toggleSelectionMode}>
              Select
            </Button>
            {view.matchCount > 0 && (
              <span className="library-range muted small" data-testid="library-range">
                {}
                {groupView
                  ? `${groupView.groupCount} session${groupView.groupCount === 1 ? '' : 's'} · ${groupView.matchCount} item${groupView.matchCount === 1 ? '' : 's'}`
                  : `Showing ${view.firstIndex}–${view.lastIndex} of ${view.matchCount}`}
                {view.filtered && view.totalCount !== view.matchCount
                  ? ` · ${view.totalCount} total`
                  : ''}
              </span>
            )}
          </div>
        )
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
        <div data-testid="library-empty">
          <EmptyState
            title="Nothing recorded yet"
            description="Tript records on its own once it recognises a game on screen. Start playing."
            action={
              <Button variant="primary" onClick={() => client.send('StartRecording')}>
                Record now
              </Button>
            }
          />
        </div>
      ) : view.matchCount === 0 ? (
        <div data-testid="library-empty-filtered">
          <FilterMismatchEmptyState total={view.totalCount} onClearFilters={clearFilters} />
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
                 {recentGroups.map((group, index) => (
                   <RecordingGroup
                     key={group.recording?.filePath ?? 'orphaned-clips'}
                     group={group}
                     variant="recent"
                     actions={groupActions}
                     thumbnailLoadingActive={thumbnailLoadingActive}
                     priority={index === 0}
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
                         previewHighlights={sessionPreviewHighlights(item, items)}
                         thumbnailLoadingActive={thumbnailLoadingActive}
                         onOpen={(item) => onOpen?.(item, groupView.resultItems, 'library')}
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
                  previewHighlights={sessionPreviewHighlights(item, items)}
                  thumbnailLoadingActive={thumbnailLoadingActive}
                  onOpen={(item) => onOpen?.(item, view.resultItems, 'library')}
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
