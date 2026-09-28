// SPDX-License-Identifier: GPL-2.0-or-later

import { useId, useRef } from 'react';
import { createPortal } from 'react-dom';
import { Button, type ButtonVariant } from './controls';
import { useModalKeyboard } from './modalKeyboard';

export function ConfirmDialog({
  title,
  notice,
  confirmLabel,
  cancelLabel = 'Cancel',
  confirmVariant = 'primary',
  onConfirm,
  onCancel,
}: {
  title: string;
  notice: string;
  confirmLabel: string;
  cancelLabel?: string;
  confirmVariant?: ButtonVariant;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);

  const titleId = useId();
  const noticeId = useId();

  useModalKeyboard(containerRef, cancelRef, onCancel);

  return createPortal(
    <div
      className="confirm-dialog"
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

        <p className="confirm-dialog-notice" id={noticeId}>
          {notice}
        </p>

        <div className="confirm-dialog-actions">
          <Button type="button" variant="ghost" ref={cancelRef} onClick={onCancel}>
            {cancelLabel}
          </Button>
          <Button type="button" variant={confirmVariant} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
