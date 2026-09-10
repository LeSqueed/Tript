// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useId, useRef, useState } from 'react';
import { summarizeNames } from './selectionModel';
import { deletionNotice } from '../trash/trashModel';
import { Button, Checkbox } from '../../components/ui/controls';

const NAME_LIMIT = 5;

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export interface DeleteConfirmation {
  title: string;
  names: string[];
  confirmLabel: string;
  permanentOnly?: boolean;
  checkbox?: {
    label: string;
    defaultChecked?: boolean;
  };
  retentionHours: number;
  affectedCount?: number;
  cascadeCount?: number;
}

const CASCADE_CHECKBOX_LABEL = 'Delete linked highlights (favourited highlights are kept)';

export interface DeleteConfirmationInput {
  names: string[];
  retentionHours: number;
  affectedCount?: number;
  hasCascade?: boolean;
  cascadeCount?: number;
  deleteLinkedHighlightsDefault?: boolean;
  permanentOnly?: boolean;
  title?: string;
  confirmLabel?: string;
}

export function makeDeleteConfirmation(input: DeleteConfirmationInput): DeleteConfirmation {
  const {
    names,
    retentionHours,
    affectedCount,
    hasCascade = false,
    cascadeCount,
    deleteLinkedHighlightsDefault,
    permanentOnly = false,
    title,
    confirmLabel,
  } = input;
  const single = names.length === 1;
  const suffix = permanentOnly ? ' for good' : '';
  return {
    title: title ?? (single
      ? `Delete "${names[0]}"${suffix}?`
      : `Delete ${names.length} items${suffix}?`),
    names,
    confirmLabel: confirmLabel ?? (permanentOnly
      ? 'Delete permanently'
      : single ? 'Move to trash' : `Move ${names.length} to trash`),
    affectedCount: affectedCount ?? names.length,
    ...(hasCascade
      ? {
          cascadeCount,
          checkbox: { label: CASCADE_CHECKBOX_LABEL, defaultChecked: deleteLinkedHighlightsDefault },
        }
      : {}),
    ...(permanentOnly ? { permanentOnly: true } : {}),
    retentionHours,
  };
}

export function ConfirmDeleteDialog({
  confirmation,
  onCancel,
  onConfirm,
}: {
  confirmation: DeleteConfirmation;
  onCancel: () => void;
  onConfirm: (permanent: boolean, checked: boolean) => void;
}) {
  const { title, names, confirmLabel, permanentOnly = false, checkbox, retentionHours } = confirmation;
  const [skipTrash, setSkipTrash] = useState(false);
  const [checked, setChecked] = useState(checkbox?.defaultChecked === true);
  const permanent = permanentOnly || skipTrash;
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
            {}
            {skipTrash && !permanentOnly ? 'Delete permanently' : confirmLabel}
          </Button>
        </div>
      </div>
    </div>
  );
}
