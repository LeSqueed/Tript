// SPDX-License-Identifier: GPL-2.0-or-later
//
// The trash — the third route, and the reason a delete in the library is recoverable at all. Every
// row states the same three facts in the same order: what it was, when it went, and when it stops
// being recoverable.

import { useCallback, useMemo, useState } from 'react';
import type { TrashEntry } from '../ipc/protocol';
import { ConfirmDeleteDialog, type DeleteConfirmation } from './library/ConfirmDeleteDialog';
import {
  addSelection,
  allSelected,
  pruneSelection,
  removeSelection,
  toggleSelection,
  type SelectionKey,
} from './library/selectionModel';
import {
  formatDeletedAt,
  formatPurgeAt,
  formatTrashDuration,
  formatTrashSize,
  retentionNotice,
  trashEntryLabel,
  trashTypeLabel,
} from './trash/trashModel';
import type { TrashController } from './trash/useTrash';
import { WorkspaceIntro, EmptyState } from './ui/Ui';

/** What a confirmed action does once the modal says yes. */
type PendingPurge = { entries: TrashEntry[]; whole: boolean };

export interface TrashViewProps {
  trash: TrashController;
  /** The clock the "deleted / purges in" phrases are measured against, in epoch seconds. */
  nowSeconds?: number;
}

export function TrashView({ trash, nowSeconds }: TrashViewProps) {
  const { entries, retentionHours, loaded } = trash;
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
    if (pending.whole) {
      return {
        title: 'Empty the trash?',
        names,
        confirmLabel: 'Empty trash',
        permanentOnly: true,
        retentionHours,
      };
    }
    return {
      title: names.length === 1 ? `Delete "${names[0]}" for good?` : `Delete ${names.length} items for good?`,
      names,
      confirmLabel: 'Delete permanently',
      permanentOnly: true,
      retentionHours,
    };
  }, [pending, retentionHours]);

  return (
    <section className="trash-view">
      <WorkspaceIntro
        eyebrow="Recovery archive"
        title="Trash"
        description="Deleted recordings stay recoverable until their retention window expires."
        aside={
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
        }
      />

      {!loaded ? (
        <div className="trash-empty" data-testid="trash-loading">
          <EmptyState
            eyebrow="Checking recovery archive"
            title="Loading trash"
            description="Tript is checking which recordings and clips are still recoverable."
          />
        </div>
      ) : entries.length === 0 ? (
        // "Nothing here" is the good state for a trash, so it is worded as reassurance rather than as
        // an absence — and it says what would put something here, which is the only thing a user
        // arriving at an empty trash by accident actually wants to know.
        <div className="trash-empty" data-testid="trash-empty">
          <EmptyState
            eyebrow="Nothing to recover"
            title="Trash is empty"
            description="The trash is empty. Recordings and clips you delete land here first, so you can put them back before the retention window closes."
          />
        </div>
      ) : (
        <>
          <div className="trash-toolbar" data-testid="trash-toolbar">
            <span className="trash-selection-count" data-testid="trash-selection-count" aria-live="polite">
              {selection.length} selected
            </span>
            <button type="button" className="btn ghost" onClick={toggleAll}>
              {everythingSelected ? 'Deselect all' : 'Select all'}
            </button>
            <button
              type="button"
              className="btn"
              onClick={restoreSelected}
              disabled={selection.length === 0}
            >
              Restore
            </button>
            <button
              type="button"
              className="btn danger"
              onClick={() => setPending({ entries: selectedEntries, whole: false })}
              disabled={selection.length === 0}
            >
              Delete permanently
            </button>
            <button
              type="button"
              className="btn danger trash-empty-action"
              onClick={() => setPending({ entries, whole: true })}
            >
              Empty trash
            </button>
          </div>

          <ul className="trash-list" data-testid="trash-list">
            {entries.map((entry) => {
              const label = trashEntryLabel(entry);
              const duration = formatTrashDuration(entry);
              const size = formatTrashSize(entry);
              const game = entry.game?.trim();
              return (
                <li key={entry.id} className="trash-row" data-testid="trash-row">
                  <input
                    type="checkbox"
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
                    <button
                      type="button"
                      className="btn ghost"
                      onClick={() => restoreOne(entry)}
                      aria-label={`Restore ${label}`}
                    >
                      Restore
                    </button>
                    <button
                      type="button"
                      className="btn danger"
                      onClick={() => setPending({ entries: [entry], whole: false })}
                      aria-label={`Delete ${label} permanently`}
                    >
                      Delete
                    </button>
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
    </section>
  );
}
