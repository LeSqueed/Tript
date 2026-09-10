// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useMemo, useState } from 'react';
import type { TrashEntry } from '../../ipc/protocol';
import { ConfirmDeleteDialog, makeDeleteConfirmation, type DeleteConfirmation } from '../library/ConfirmDeleteDialog';
import {
  addSelection,
  allSelected,
  pruneSelection,
  removeSelection,
  toggleSelection,
  type SelectionKey,
} from '../library/selectionModel';
import {
  formatDeletedAt,
  formatPurgeAt,
  formatTrashDuration,
  formatTrashSize,
  retentionNotice,
  trashEntryLabel,
  trashTypeLabel,
} from './trashModel';
import type { TrashController } from './useTrash';
import { ActionBar, EmptyState, FilterMismatchEmptyState } from '../ui/Ui';
import { Button, Checkbox } from '../ui/controls';

type PendingPurge = { entries: TrashEntry[]; whole: boolean };

export interface TrashViewProps {
  trash: TrashController;
  nowSeconds?: number;
  entries?: TrashEntry[];
  filtered?: boolean;
  onClearFilters?: () => void;
}

export function TrashList({ trash, nowSeconds, entries: visibleEntries, filtered = false, onClearFilters }: TrashViewProps) {
  const { entries: sourceEntries, retentionHours, loaded } = trash;
  const entries = visibleEntries ?? sourceEntries;
  const now = nowSeconds ?? Date.now() / 1000;

  const [selected, setSelected] = useState<SelectionKey[]>([]);
  const [pending, setPending] = useState<PendingPurge | null>(null);

  const liveIds = useMemo(() => new Set(entries.map((entry) => entry.id)), [entries]);
  const selection = useMemo(() => pruneSelection(selected, liveIds), [selected, liveIds]);
  const allIds = useMemo(() => entries.map((entry) => entry.id), [entries]);
  const everythingSelected = allSelected(allIds, selection);
  const selectedEntries = useMemo(
    () => entries.filter((entry) => selection.includes(entry.id)),
    [entries, selection],
  );

  const toggleEntry = useCallback((entry: TrashEntry) => {
    setSelected((previous) => toggleSelection(previous, entry.id));
  }, []);

  const toggleAll = useCallback(() => {
    setSelected((previous) =>
      everythingSelected ? removeSelection(previous, allIds) : addSelection(previous, allIds),
    );
  }, [everythingSelected, allIds]);

  const restoreOne = useCallback((entry: TrashEntry) => trash.restore([entry.id]), [trash]);

  const restoreSelected = useCallback(() => {
    trash.restore(selection);
    setSelected([]);
  }, [trash, selection]);

  const cancel = useCallback(() => setPending(null), []);

  const confirm = useCallback(() => {
    if (pending === null) {
      return;
    }
    if (pending.whole) {
      trash.emptyTrash();
      setSelected([]);
    } else {
      const ids = pending.entries.map((entry) => entry.id);
      trash.purge(ids);
      setSelected((previous) => removeSelection(previous, ids));
    }
    setPending(null);
  }, [pending, trash]);

  const confirmation: DeleteConfirmation | null = useMemo(() => {
    if (pending === null) {
      return null;
    }
    const names = pending.entries.map(trashEntryLabel);
    return makeDeleteConfirmation({
      names,
      retentionHours,
      permanentOnly: true,
      ...(pending.whole ? { title: 'Empty the trash?', confirmLabel: 'Empty trash' } : {}),
    });
  }, [pending, retentionHours]);

  return (
    <div className="trash-view">
      <div className="trash-intro-meta">
        {entries.length > 0 && (
          <span className="muted small" data-testid="trash-count">
            {entries.length} item{entries.length === 1 ? '' : 's'}
          </span>
        )}
        <span className="trash-retention muted small" data-testid="trash-retention">
          {retentionNotice(retentionHours)}
        </span>
      </div>

      {!loaded ? (
        <div className="trash-empty" data-testid="trash-loading">
          <EmptyState
            title="Loading trash"
            description="Tript is checking which recordings and clips are still recoverable."
          />
        </div>
      ) : entries.length === 0 && filtered ? (
        <div className="trash-empty" data-testid="trash-empty-filtered">
          <FilterMismatchEmptyState total={sourceEntries.length} noun="deleted item" onClearFilters={onClearFilters} />
        </div>
      ) : entries.length === 0 ? (
        <div className="trash-empty" data-testid="trash-empty">
          <EmptyState
            title="Trash is empty"
            description="Recordings and clips you delete land here first, so you can put them back before the retention window closes."
          />
        </div>
      ) : (
        <>
          <ActionBar
            data-testid="trash-toolbar"
            leading={
              <span className="action-bar-count" data-testid="trash-selection-count" aria-live="polite">
                {selection.length} selected
              </span>
            }
            trailing={<Button variant="danger" onClick={() => setPending({ entries: sourceEntries, whole: true })}>Empty trash</Button>}
          >
            <Button variant="ghost" onClick={toggleAll}>
              {everythingSelected ? 'Deselect all' : 'Select all'}
            </Button>
            <Button variant="primary" onClick={restoreSelected} disabled={selection.length === 0}>
              Restore
            </Button>
            <Button
              variant="danger"
              onClick={() => setPending({ entries: selectedEntries, whole: false })}
              disabled={selection.length === 0}
            >
              Delete permanently
            </Button>
          </ActionBar>

          <ul className="trash-list" data-testid="trash-list">
            {entries.map((entry) => {
              const label = trashEntryLabel(entry);
              const duration = formatTrashDuration(entry);
              const size = formatTrashSize(entry);
              const game = entry.game?.trim();
              return (
                <li key={entry.id} className="trash-row" data-testid="trash-row">
                  <Checkbox
                    className="trash-row-select"
                    checked={selection.includes(entry.id)}
                    aria-label={`Select ${label}`}
                    onChange={() => toggleEntry(entry)}
                  />
                  <div className="trash-row-main">
                    <span className="trash-row-title" title={label}>
                      {label}
                    </span>
                    <span className="trash-row-chips">
                      <span className="pill">{trashTypeLabel(entry)}</span>
                      {game ? <span className="pill pill-muted">{game}</span> : null}
                      {duration !== null && <span className="pill pill-muted">{duration}</span>}
                      {size !== null && <span className="pill pill-muted">{size}</span>}
                    </span>
                    <span className="trash-row-times muted small">
                      {formatDeletedAt(entry, now)} · {formatPurgeAt(entry, now)}
                    </span>
                  </div>
                  <div className="trash-row-actions">
                    <Button variant="ghost"

                      onClick={() => restoreOne(entry)}
                      aria-label={`Restore ${label}`}
                    >
                      Restore
                    </Button>
                    <Button variant="danger"

                      onClick={() => setPending({ entries: [entry], whole: false })}
                      aria-label={`Delete ${label} permanently`}
                    >
                      Delete
                    </Button>
                  </div>
                </li>
              );
            })}
          </ul>
        </>
      )}

      {confirmation && (
        <ConfirmDeleteDialog confirmation={confirmation} onCancel={cancel} onConfirm={confirm} />
      )}
    </div>
  );
}
