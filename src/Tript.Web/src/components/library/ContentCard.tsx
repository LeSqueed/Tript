// SPDX-License-Identifier: GPL-2.0-or-later

import { useState, type CSSProperties, type ReactNode } from 'react';
import type { ContentItem } from '../../ipc/protocol';
import {
  formatBytes,
  formatDateChip,
  formatDurationChip,
  itemGame,
  itemLabel,
  typeLabel,
  UNKNOWN_GAME_LABEL,
} from './libraryModel';
import { Icon } from '../ui/Icon';
import { Checkbox } from '../../components/ui/controls';
import { ContentThumbnail } from './ContentThumbnail';

export function ContentCard({
  item,
  onOpen,
  onDelete,
  onToggleFavorite,
  selectable = false,
  selected = false,
  onToggleSelected,
  variant = 'grid',
  action,
  priority = false,
  thumbnailLoadingActive = true,
  clipsCount = 0,
  highlightsCount = 0,
  previewHighlights = [],
  sizeBytes,
}: {
  item: ContentItem;
  onOpen?: (item: ContentItem) => void;
  onDelete?: (item: ContentItem) => void;
  onToggleFavorite?: (item: ContentItem) => void;
  selectable?: boolean;
  selected?: boolean;
  onToggleSelected?: (item: ContentItem) => void;
  variant?: 'grid' | 'wide';
  priority?: boolean;
  thumbnailLoadingActive?: boolean;
  clipsCount?: number;
  highlightsCount?: number;
  previewHighlights?: ContentItem[];
  sizeBytes?: number;
  action?: ReactNode;
}) {
  const label = itemLabel(item);
  const game = itemGame(item) ?? UNKNOWN_GAME_LABEL;
  const duration = formatDurationChip(item);
  const size = formatBytes(sizeBytes ?? item.fileSizeBytes);
  const missingVideo = item.videoMissing === true;
  const highlightsOnly = item.highlightsOnly === true;
  const liveRecording = item.recording === true;
  const showThumbnail = thumbnailLoadingActive && !missingVideo && !highlightsOnly && !liveRecording && item.filePath.length > 0;
  const preview = previewHighlights.slice(0, 3);
  const canOpen = !liveRecording || highlightsCount > 0;

  return (
    <div
      className={[
        'content-card-shell',
        variant === 'wide' ? 'content-card-shell--wide' : '',
        missingVideo || highlightsOnly ? 'content-card-shell--missing' : '',
        liveRecording ? 'content-card-shell--recording' : '',
        selected ? 'selected' : '',
      ]
        .filter(Boolean)
        .join(' ')}
    >
      <button
        type="button"
        className={variant === 'wide' ? 'content-card content-card--wide' : 'content-card'}
        data-testid="content-card"
        onClick={() => {
          if (canOpen) onOpen?.(item);
        }}
        aria-label={canOpen ? `Open ${label}` : `${highlightsOnly ? 'Buffering' : 'Recording'} in progress: ${label}`}
      >
        <span className="content-card-thumb">
          {liveRecording ? (
            <LiveRecordingPreview
              key={preview.map((highlight) => highlight.filePath).join('|')}
              highlights={preview}
              thumbnailLoadingActive={thumbnailLoadingActive}
            />
          ) : highlightsOnly ? (
            <MissingVideoPreview
              key={preview.map((highlight) => highlight.filePath).join('|')}
              highlights={preview}
              thumbnailLoadingActive={thumbnailLoadingActive}
              message="Highlights-only session"
              testId="content-card-highlights-only"
            />
          ) : missingVideo ? (
            <MissingVideoPreview
              key={preview.map((highlight) => highlight.filePath).join('|')}
              highlights={preview}
              thumbnailLoadingActive={thumbnailLoadingActive}
            />
          ) : showThumbnail ? (
            <ContentThumbnail
              key={item.filePath}
              filePath={item.filePath}
              className="content-card-image"
              loading={priority ? 'eager' : 'lazy'}
              fetchPriority={priority ? 'high' : 'low'}
              fallback={
                <span className="content-card-placeholder" data-testid="content-card-placeholder">
                  <Icon name="play" size={22} />
                </span>
              }
            />
          ) : (
            <span className="content-card-placeholder" data-testid="content-card-placeholder">
              <Icon name="play" size={22} />
            </span>
          )}
          <span className="content-card-overlay">
            <span className="content-card-title" title={label}>
              {label}
            </span>
            <span className="content-card-chips">
              <span className="pill content-card-type">{typeLabel(item)}</span>
              {liveRecording && <span className="pill content-card-recording-chip">{highlightsOnly ? 'Buffering' : 'Recording'}</span>}
              {highlightsOnly && !liveRecording && <span className="pill content-card-missing-chip">Highlights-only session</span>}
              {missingVideo && <span className="pill content-card-missing-chip">Highlights only</span>}
              <span className="pill pill-muted">{game}</span>
              <span className="pill pill-muted">{formatDateChip(item)}</span>
              {clipsCount > 0 && <span className="pill pill-muted">Clips: {clipsCount}</span>}
              {item.automaticClipsProcessing && (
                <span className="pill pill-muted">
                  {item.automaticClipsPaused ? 'Highlights paused' : 'Creating highlights'}
                </span>
              )}
              {size !== null && <span className="pill pill-muted">{size}</span>}
            </span>
          </span>
          {duration !== null && <span className="content-card-duration">{duration}</span>}
          {(highlightsCount > 0 || item.automaticClipsProcessing) && (
            <span
              className={item.automaticClipsProcessing ? 'content-card-highlights content-card-highlights--active' : 'content-card-highlights'}
              aria-label={
                item.automaticClipsProcessing
                  ? 'Highlights are being created'
                  : `${highlightsCount} highlight${highlightsCount === 1 ? '' : 's'}`
              }
              data-testid="content-card-highlights"
            >
              <Icon name="clip" size={14} />
            </span>
          )}
        </span>
      </button>

      {action && <div className="content-card-action">{action}</div>}

      {selectable && !liveRecording && (
        <Checkbox
          className="content-card-select"
          checked={selected}
          aria-label={`Select ${label}`}
          onChange={() => onToggleSelected?.(item)}
        />
      )}

      {}
      {onDelete && !liveRecording && (
        <button
          type="button"
          className="content-card-delete"
          onClick={() => onDelete(item)}
          aria-label={`Delete ${label}`}
        >
          <Icon name="trash" size={15} />
        </button>
      )}

      {onToggleFavorite && !missingVideo && !highlightsOnly && !liveRecording && (
        <button
          type="button"
          className={item.favorite ? 'content-card-favorite active' : 'content-card-favorite'}
          onClick={() => onToggleFavorite(item)}
          aria-label={`${item.favorite ? 'Remove' : 'Add'} ${label} ${item.favorite ? 'from' : 'to'} favorites`}
          aria-pressed={item.favorite === true}
        >
          <Icon name="star" size={15} filled={item.favorite === true} />
        </button>
      )}
    </div>
  );
}

function MissingVideoPreview({
  highlights,
  thumbnailLoadingActive,
  message = 'Source video unavailable',
  testId = 'content-card-missing',
}: {
  highlights: ContentItem[];
  thumbnailLoadingActive: boolean;
  message?: string;
  testId?: string;
}) {
  const [loaded, setLoaded] = useState<Set<string>>(() => new Set());

  return (
    <span className="content-card-missing-preview" data-testid={`${testId}-preview`}>
      <span className="content-card-missing-fallback" data-testid={`${testId}-fallback`}>
        <Icon name="clip" size={22} />
        <span>{message}</span>
      </span>
      {thumbnailLoadingActive && highlights.length > 0 && (
        <span
          className="content-card-preview-grid"
          style={{ '--preview-count': highlights.length } as CSSProperties}
        >
          {highlights.map((highlight) => (
            <ContentThumbnail
              key={highlight.filePath}
              filePath={highlight.filePath}
              className={loaded.has(highlight.filePath) ? 'content-card-preview-image loaded' : 'content-card-preview-image'}
              loading="lazy"
              fetchPriority="low"
              onLoad={() => setLoaded((current) => new Set(current).add(highlight.filePath))}
            />
          ))}
        </span>
      )}
    </span>
  );
}

function LiveRecordingPreview({
  highlights,
  thumbnailLoadingActive,
}: {
  highlights: ContentItem[];
  thumbnailLoadingActive: boolean;
}) {
  const [loaded, setLoaded] = useState<Set<string>>(() => new Set());

  return (
    <span className="content-card-recording-preview" data-testid="content-card-recording-preview">
      <span className="content-card-recording-fallback" data-testid="content-card-recording-fallback">
        <span className="rec-dot recording" />
        <span>Recording in progress</span>
      </span>
      {thumbnailLoadingActive && highlights.length > 0 && (
        <span
          className="content-card-preview-grid"
          style={{ '--preview-count': highlights.length } as CSSProperties}
        >
          {highlights.map((highlight) => (
            <ContentThumbnail
              key={highlight.filePath}
              filePath={highlight.filePath}
              className={loaded.has(highlight.filePath) ? 'content-card-preview-image loaded' : 'content-card-preview-image'}
              loading="lazy"
              fetchPriority="low"
              onLoad={() => setLoaded((current) => new Set(current).add(highlight.filePath))}
            />
          ))}
        </span>
      )}
    </span>
  );
}
