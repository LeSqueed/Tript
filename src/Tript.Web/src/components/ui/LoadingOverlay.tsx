import { useEffect, useState } from 'react';
import { Button } from './controls';
import './LoadingOverlay.css';

interface LoadingOverlayProps {
  title: string;
  description?: string;
  progress?: number | null;
  delayMs?: number;
  /** When set with onCancel, renders a cancel action in the dialog. */
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

  useEffect(() => {
    if (delayMs <= 0) return;
    const timer = window.setTimeout(() => setVisible(true), delayMs);
    return () => window.clearTimeout(timer);
  }, [delayMs]);

  if (!visible) return null;
  const percent = progress == null ? null : Math.max(0, Math.min(100, Math.round(progress)));

  return (
    <div className="ui-loading-backdrop" role="presentation">
      <section className="ui-loading-dialog" role="dialog" aria-modal="true" aria-labelledby="ui-loading-title">
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
