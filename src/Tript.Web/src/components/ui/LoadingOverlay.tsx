import { useEffect, useRef, useState } from 'react';
import { Button } from './controls';
import './LoadingOverlay.css';

interface LoadingOverlayProps {
  title: string;
  description?: string;
  progress?: number | null;
  delayMs?: number;
  cancelLabel?: string;
  onCancel?: () => void;
}

export function LoadingOverlay({
  title,
  description,
  progress = null,
  delayMs = 250,
  cancelLabel,
  onCancel,
}: LoadingOverlayProps) {
  const [visible, setVisible] = useState(delayMs <= 0);
  const dialogRef = useRef<HTMLElement>(null);
  const cancelRef = useRef(onCancel);
  cancelRef.current = onCancel;

  useEffect(() => {
    if (delayMs <= 0) return;
    const timer = window.setTimeout(() => setVisible(true), delayMs);
    return () => window.clearTimeout(timer);
  }, [delayMs]);

  useEffect(() => {
    if (!visible) return;
    const previous = document.activeElement as HTMLElement | null;
    const dialog = dialogRef.current;
    dialog?.focus();
    const handleKeyDown = (event: KeyboardEvent) => {
      if (!dialog?.contains(event.target as Node)) return;
      if (event.key === 'Escape' && cancelRef.current) {
        event.preventDefault();
        event.stopPropagation();
        cancelRef.current();
        return;
      }
      if (event.key !== 'Tab') return;
      const focusable = Array.from(dialog.querySelectorAll<HTMLElement>(
        'button, input, select, textarea, [href], [tabindex]:not([tabindex="-1"])',
      )).filter((element) => !element.hasAttribute('disabled'));
      if (focusable.length === 0) {
        event.preventDefault();
        return;
      }
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (!focusable.includes(document.activeElement as HTMLElement)) {
        event.preventDefault();
        (event.shiftKey ? last : first).focus();
      } else if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('keydown', handleKeyDown);
      previous?.focus();
    };
  }, [visible]);

  if (!visible) return null;
  const percent = progress == null ? null : Math.max(0, Math.min(100, Math.round(progress)));

  return (
    <div className="ui-loading-backdrop" role="presentation">
      <section ref={dialogRef} tabIndex={-1} className="ui-loading-dialog" role="dialog" aria-modal="true" aria-labelledby="ui-loading-title">
        <span className="ui-loading-spinner" aria-hidden="true" />
        <div className="ui-loading-copy">
          <h3 id="ui-loading-title">{title}</h3>
          {description && <p className="muted small">{description}</p>}
        </div>
        {percent != null && (
          <div
            className="ui-loading-progress"
            role="progressbar"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={percent}
          >
            <span style={{ width: `${percent}%` }} />
          </div>
        )}
        {cancelLabel && onCancel && (
          <Button variant="ghost" className="ui-loading-cancel" onClick={onCancel}>
            {cancelLabel}
          </Button>
        )}
      </section>
    </div>
  );
}
