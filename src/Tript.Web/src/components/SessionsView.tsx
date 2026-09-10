// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useMemo, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { ConnectionState } from '../ipc/websocketClient';
import type { ContentItem } from '../ipc/protocol';
import { Button, Toggle, type SelectOption } from './ui/controls';
import { ContentCard } from './library/ContentCard';
import { ConfirmDeleteDialog, makeDeleteConfirmation, type DeleteConfirmation } from './library/ConfirmDeleteDialog';
import { LibraryPagination } from './library/LibraryPagination';
import { LibrarySelectFilters } from './library/LibraryToolbar';
import {
  ANY_GAME,
  availableGames,
  cascadableLinkedHighlights,
  DATE_OPTIONS,
  DEFAULT_LIBRARY_QUERY,
  deriveSessions,
  itemLabel,
  lacksMainVideo,
  NO_GAME,
  recordingChildCounts,
  sessionPreviewHighlights,
  SORT_OPTIONS,
  UNKNOWN_GAME_LABEL,
  type LibraryQuery,
} from './library/libraryModel';
import { selectionKey } from './library/selectionModel';
import { DEFAULT_RETENTION_HOURS } from './trash/trashModel';
import { EmptyState, FilterMismatchEmptyState } from './ui/Ui';
import { useWatchedGames } from './recorder/useWatchedGames';

const SESSIONS_QUERY: LibraryQuery = { ...DEFAULT_LIBRARY_QUERY, type: 'sessions' };

export interface SessionsViewProps {
  client: IpcClient;
  items: ContentItem[];
  thumbnailLoadingActive?: boolean;
  connectionState?: ConnectionState;
  contentLoaded?: boolean;
  onOpen?: (item: ContentItem, resultItems: ContentItem[]) => void;
  nowSeconds?: number;
  retentionHours?: number;
  deleteLinkedHighlightsByDefault?: boolean;
}

export function SessionsView({
  client,
  items,
  thumbnailLoadingActive = true,
  connectionState,
  contentLoaded = true,
  onOpen,
  nowSeconds,
  retentionHours = DEFAULT_RETENTION_HOURS,
  deleteLinkedHighlightsByDefault = false,
}: SessionsViewProps) {
  const [query, setQuery] = useState<LibraryQuery>(SESSIONS_QUERY);
  const [pendingDelete, setPendingDelete] = useState<ContentItem | null>(null);
  const now = nowSeconds ?? Date.now() / 1000;
  const view = useMemo(() => deriveSessions(items, query, now), [items, query, now]);
  const watchedGames = useWatchedGames(client);
  const recordingCounts = useMemo(() => recordingChildCounts(items), [items]);

  const updateQuery = useCallback((patch: Partial<LibraryQuery>) => {
    setQuery((previous) => ({ ...previous, ...patch, page: 1 }));
  }, []);

  const goToPage = useCallback((page: number) => {
    setQuery((previous) => ({ ...previous, page }));
  }, []);

  const clearFilters = useCallback(() => {
    setQuery((previous) => ({
      ...SESSIONS_QUERY,
      sort: previous.sort,
      pageSize: previous.pageSize,
    }));
  }, []);

  const gameOptions: SelectOption[] = useMemo(() => {
    const games = availableGames(items);
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
  }, [items, query.game]);

  const toggleFavorite = useCallback((item: ContentItem) => {
    client.send('ToggleFavorite', {
      contentType: item.contentType,
      filePath: item.filePath,
      favorite: item.favorite !== true,
    });
  }, [client]);

  const requestDelete = useCallback((item: ContentItem) => setPendingDelete(item), []);

  const cancelDelete = useCallback(() => setPendingDelete(null), []);

  const confirmDelete = useCallback((permanent: boolean, deleteLinkedHighlights: boolean) => {
    if (pendingDelete) {
      client.send('DeleteContent', {
        contentType: pendingDelete.contentType,
        fileName: pendingDelete.filePath,
        ...(deleteLinkedHighlights ? { deleteLinkedHighlights: true } : {}),
        ...(permanent ? { permanent: true } : {}),
      });
    }
    setPendingDelete(null);
  }, [client, pendingDelete]);

  const confirmation: DeleteConfirmation | null = pendingDelete
    ? makeDeleteConfirmation({
        names: [itemLabel(pendingDelete)],
        retentionHours,
        affectedCount: lacksMainVideo(pendingDelete) ? 0 : 1,
        hasCascade: true,
        cascadeCount: cascadableLinkedHighlights(pendingDelete, items).length,
        deleteLinkedHighlightsDefault: deleteLinkedHighlightsByDefault,
      })
    : null;

  return (
    <section className="library-view sessions-view" data-testid="sessions-view">
      <div className="library-toolbar">
        <div className="library-filters">
          <LibrarySelectFilters
            query={query}
            gameOptions={gameOptions}
            dateOptions={DATE_OPTIONS}
            sortOptions={SORT_OPTIONS}
            onQueryChange={updateQuery}
          />
          <Toggle
            checked={query.favoriteOnly}
            onChange={(checked) => updateQuery({ favoriteOnly: checked })}
            label="Favourites only"
          />
          {view.filtered && (
            <Button variant="ghost" className="library-clear" onClick={clearFilters}>
              Clear filters
            </Button>
          )}
        </div>
      </div>

      {view.totalCount > 0 && (
        <div className="library-selection">
          {view.matchCount > 0 && (
            <span className="library-range muted small" data-testid="sessions-range">
              Showing {view.firstIndex}–{view.lastIndex} of {view.matchCount} session{view.matchCount === 1 ? '' : 's'}
              {view.filtered && view.totalCount !== view.matchCount
                ? ` · ${view.totalCount} total`
                : ''}
            </span>
          )}
        </div>
      )}

      {view.totalCount === 0 && connectionState === 'disconnected' ? (
        <div data-testid="sessions-unavailable">
          <EmptyState
            title="Sessions unavailable"
            description="Tript cannot reach the capture host right now. Reconnect to load your sessions."
          />
        </div>
      ) : view.totalCount === 0 && (!contentLoaded || connectionState === 'connecting') ? (
        <div data-testid="sessions-loading">
          <EmptyState
            title="Loading your sessions"
            description="Tript is asking the capture host for your recordings."
          />
        </div>
      ) : view.totalCount === 0 ? (
        <div data-testid="sessions-empty">
          <EmptyState
            title="No sessions yet"
            description={
              watchedGames.length > 0
                ? `Tript is watching for ${watchedGames.join(', ')} — start playing and it records on its own.`
                : 'Tript records a session on its own once it recognises a game on screen. Start playing.'
            }
            action={
              <Button variant="primary" onClick={() => client.send('StartRecording')}>
                Record now
              </Button>
            }
          />
        </div>
      ) : view.matchCount === 0 ? (
        <div data-testid="sessions-empty-filtered">
          <FilterMismatchEmptyState total={view.totalCount} noun="session" onClearFilters={clearFilters} />
        </div>
      ) : (
        <ul className="library-grid" data-testid="sessions-grid">
          {view.items.map((item) => (
            <li key={selectionKey(item)}>
              <ContentCard
                item={item}
                clipsCount={recordingCounts.get(item.filePath)?.clips ?? 0}
                highlightsCount={recordingCounts.get(item.filePath)?.highlights ?? 0}
                previewHighlights={sessionPreviewHighlights(item, items)}
                thumbnailLoadingActive={thumbnailLoadingActive}
                onOpen={(item) => onOpen?.(item, view.resultItems)}
                onDelete={requestDelete}
                onToggleFavorite={toggleFavorite}
              />
            </li>
          ))}
        </ul>
      )}

      {view.pageCount > 1 && (
        <LibraryPagination
          page={view.page}
          pageCount={view.pageCount}
          onPageChange={goToPage}
        />
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
