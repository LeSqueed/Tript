// SPDX-License-Identifier: GPL-2.0-or-later

import type { CSSProperties } from 'react';
import { Button } from '../controls';
import { Icon, type IconName } from '../Icon';
import type { ToastItem, ToastKind } from './toastModel';

const TONE_CLASS: Record<ToastKind, string> = {
  success: 'toast-success',
  error: 'toast-error',
  warning: 'toast-warning',
  info: 'toast-info',
};

const KIND_ICON: Record<ToastKind, IconName> = {
  success: 'checkCircle',
  error: 'errorCircle',
  warning: 'alertTriangle',
  info: 'infoCircle',
};

const RING_LENGTH = 59.69;

export interface ToastProps {
  item: ToastItem;
  onDismissSelf: (id: number) => void;
  onHover: (id: number, hovered: boolean) => void;
}

export function Toast({ item, onDismissSelf, onHover }: ToastProps) {
  const classes = ['toast', TONE_CLASS[item.kind]];
  if (item.state === 'leaving') {
    classes.push('toast-leaving');
  }
  return (
    <div
      className={classes.join(' ')}
      data-testid={item.testId}
      role={item.kind === 'error' ? 'alert' : 'status'}
      style={{ '--toast-duration': `${item.duration}ms` } as CSSProperties}
      onPointerEnter={() => onHover(item.id, true)}
      onPointerLeave={() => onHover(item.id, false)}
      onFocusCapture={() => onHover(item.id, true)}
      onBlurCapture={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          onHover(item.id, false);
        }
      }}
    >
      <Icon name={KIND_ICON[item.kind]} size={20} className="toast-icon" />
      <div className="toast-body">
        {item.title !== undefined && <strong className="toast-title">{item.title}</strong>}
        <span className="toast-message">{item.message}</span>
        {item.note !== undefined && <span className="toast-note" role="alert">{item.note}</span>}
        {item.actions !== undefined && item.actions.length > 0 && (
          <div className="toast-actions">
            {item.actions.map((action) => (
              <Button
                key={action.label}
                size="small"
                variant={action.variant ?? 'primary'}
                disabled={action.disabled}
                onClick={action.onClick}
              >
                {action.label}
              </Button>
            ))}
          </div>
        )}
      </div>
      {item.dismissible !== false && <button
        type="button"
        className="toast-dismiss"
        aria-label="Dismiss notification"
        onClick={() => onDismissSelf(item.id)}
      >
        {item.duration > 0 && (
          <svg className="toast-timer" viewBox="0 0 22 22" aria-hidden="true" focusable="false">
            <circle cx="11" cy="11" r="9.5" strokeDasharray={RING_LENGTH} />
          </svg>
        )}
        <Icon name="close" size={16} />
      </button>}
    </div>
  );
}
