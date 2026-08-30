// SPDX-License-Identifier: GPL-2.0-or-later
//
// The delete confirmation. Mandatory for every delete, single or bulk, from the library and from
// the trash — deleting a recording is the one action in this app that destroys work, and the only
// thing standing between a mis-click and that is this panel.

import { useEffect, useId, useRef, useState } from 'react';
import { summarizeNames } from './selectionModel';
import { deletionNotice } from '../trash/trashModel';
import { Button, Checkbox } from '../../components/ui/controls';

/** Above this many, the items are summarised instead of listed. */
const NAME_LIMIT = 5;

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export interface DeleteConfirmation {
  /** The dialog's heading — what the user asked for, e.g. "Delete 3 items". */
  title: string;
  /** Display names of the affected items, in list order. */
  names: string[];
  /** Label on the destructive button while the trash path is taken. */
  confirmLabel: string;
  /**
   * The item is already in the trash: there is no trash path left, so the skip-trash checkbox is not
   * offered and the confirm always sends `permanent`.
   */
  permanentOnly?: boolean;
  /** An independent, ordinary checkbox offered for this deletion, when applicable. */
  checkbox?: {
    label: string;
    defaultChecked?: boolean;
  };
  retentionHours: number;
  /**
   * How many items actually move, when that number is not `names.length`. A missing-video session's
   * placeholder names itself but deletes nothing on its own, so it is not counted. The trash sentence
   * reads this, so the count it quotes never disagrees with what the button does.
   */
  affectedCount?: number;
  /**
   * How many linked non-favourited automatic highlights a checked cascade checkbox adds. Only read
   * when this dialog has a checkbox; the caller counts them (mirroring the backend's eligibility) so
   * the sentence the user reads about "how many items" matches what ticking the box will do.
   */
  cascadeCount?: number;
}

export function ConfirmDeleteDialog({
  confirmation,
  onCancel,
  onConfirm,
}: {
  confirmation: DeleteConfirmation;
  onCancel: () => void;
  /** Called with the permanent choice and the optional ordinary checkbox choice. */
  onConfirm: (permanent: boolean, checked: boolean) => void;
}) {
  const { title, names, confirmLabel, permanentOnly = false, checkbox, retentionHours } = confirmation;
  const [skipTrash, setSkipTrash] = useState(false);
  const [checked, setChecked] = useState(checkbox?.defaultChecked === true);
  const permanent = permanentOnly || skipTrash;
  // `names.length` is what the caller listed, but the list may not be the deletion: a cascaded link
  // can delete highlights the dialog never named. The notice counts what the delete really takes.
  const affectedCount =
    (confirmation.affectedCount ?? names.length) + (checkbox && checked ? (confirmation.cascadeCount ?? 0) : 0);

  const containerRef = useRef<HTMLDivElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);
  const onCancelRef = useRef(onCancel);
  onCancelRef.current = onCancel;

  const titleId = useId();
  const noticeId = useId();

  useEffect(() => {
    const previouslyFocused = document.activeElement;
    cancelRef.current?.focus();
    return () => {
      if (previouslyFocused instanceof HTMLElement && document.contains(previouslyFocused)) {
        previouslyFocused.focus();
      }
    };
  }, []);

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent): void {
      const container = containerRef.current;
      if (!container) {
        return;
      }
      if (event.key === 'Escape') {
        event.preventDefault();
        // Stop here: the dialog can be opened over the player overlay, which also closes on Escape.
        event.stopPropagation();
        onCancelRef.current();
        return;
      }
      if (event.key !== 'Tab') {
        return;
      }
      const focusable = [...container.querySelectorAll<HTMLElement>(FOCUSABLE)];
      if (focusable.length === 0) {
        return;
      }
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      const active = document.activeElement;
      if (!(active instanceof HTMLElement) || !container.contains(active)) {
        event.preventDefault();
        first.focus();
        return;
      }
      if (!event.shiftKey && active === last) {
        event.preventDefault();
        first.focus();
      } else if (event.shiftKey && active === first) {
        event.preventDefault();
        last.focus();
      }
    }

    document.addEventListener('keydown', onKeyDown, true);
    return () => document.removeEventListener('keydown', onKeyDown, true);
  }, []);

  return (
    <div
      className="confirm-dialog"
      data-testid="confirm-delete"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
      aria-describedby={noticeId}
      ref={containerRef}
    >
      <div className="confirm-dialog-panel">
        <h2 className="confirm-dialog-title" id={titleId}>
          {title}
        </h2>

        <p className="confirm-dialog-notice" id={noticeId} data-testid="confirm-delete-notice">
          {deletionNotice(affectedCount, permanent, retentionHours)}
        </p>

        {names.length <= NAME_LIMIT ? (
          <ul className="confirm-dialog-items" data-testid="confirm-delete-items">
            {names.map((name, index) => (
              <li key={`${name}:${index}`} title={name}>
                {name}
              </li>
            ))}
          </ul>
        ) : (
          <p className="confirm-dialog-summary muted" data-testid="confirm-delete-summary">
            {summarizeNames(names, 3)}
          </p>
        )}

        {!permanentOnly && (
          <label className="confirm-dialog-skip">
            <Checkbox
              checked={skipTrash}
              onChange={setSkipTrash}
            />
            <span>Delete permanently (skip trash)</span>
          </label>
        )}

        {checkbox && (
          <label className="confirm-dialog-skip">
            <Checkbox checked={checked} onChange={setChecked} />
            <span>{checkbox.label}</span>
          </label>
        )}

        <div className="confirm-dialog-actions">
          <Button variant="ghost"  ref={cancelRef} onClick={onCancel}>
            Cancel
          </Button>
          <Button variant="danger"
            
            data-testid="confirm-delete-confirm"
            onClick={() => onConfirm(permanent, checked)}
          >
            {/* Ticking the checkbox rewrites the button too: the label the user presses must
                describe what ticking it changed. A permanent-only caller already named its own
                action ("Empty trash"), so it keeps it. */}
            {skipTrash && !permanentOnly ? 'Delete permanently' : confirmLabel}
          </Button>
        </div>
      </div>
    </div>
  );
}
