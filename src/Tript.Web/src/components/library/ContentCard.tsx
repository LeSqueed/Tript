// SPDX-License-Identifier: GPL-2.0-or-later
//
// One card in the library grid: a 16:9 thumbnail, the title, and chips for what the item is, which
// game it came from, when it was recorded and how big it is.
//
// The card's *open* affordance is a `<button>` rather than a div with a click handler, so it is
// keyboard-reachable and activatable for free (Enter/Space) and the player overlay can restore focus
// to it on close. The select checkbox and the delete button are its SIBLINGS inside a positioned
// wrapper, not its children: a button inside a button is invalid markup and browsers disagree about
// which one a click activates.

import { useState, type CSSProperties, type ReactNode } from 'react';
import type { ContentItem } from '../../ipc/protocol';
import {
  formatDateChip,
  formatDurationChip,
  formatSizeChip,
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
}: {
  item: ContentItem;
  /** The library's open seam: called with the item the user activated. */
  onOpen?: (item: ContentItem) => void;
  /** The library's delete seam. Absent means the card offers no delete at all. */
  onDelete?: (item: ContentItem) => void;
  onToggleFavorite?: (item: ContentItem) => void;
  /** Whether the grid is in selection mode — the checkbox only exists then. */
  selectable?: boolean;
  selected?: boolean;
  onToggleSelected?: (item: ContentItem) => void;
  /**
   * `wide` lays the thumbnail beside the body instead of above it, for the library's hero. It is a
   * variant rather than a separate component because a hero that reimplemented the card lost the
   * delete, favourite and select affordances the moment it was written.
   */
  variant?: 'grid' | 'wide';
  /** Recent sessions are above the fold and should not wait for intersection before painting. */
  priority?: boolean;
  /** False while the mounted library is hidden behind playback or session review. */
  thumbnailLoadingActive?: boolean;
  /** Number of clips cut from this recording, shown as a metadata chip when present. */
  clipsCount?: number;
  /** Number of generated highlights, shown as an affordance on the thumbnail. */
  highlightsCount?: number;
  /** Linked automatic highlights, already in timeline order, for a missing-video preview. */
  previewHighlights?: ContentItem[];
  /**
   * An extra affordance, rendered inside the shell beside the card rather than within it — a button
   * inside a button is invalid markup, and the delete and favourite controls are siblings for the
   * same reason. The hero's Review action uses it so the action sits with the item it acts on.
   */
  action?: ReactNode;
}) {
  const label = itemLabel(item);
  const game = itemGame(item) ?? UNKNOWN_GAME_LABEL;
  const duration = formatDurationChip(item);
  const size = formatSizeChip(item);
  const missingVideo = item.videoMissing === true;
  const highlightsOnly = item.highlightsOnly === true;
  const liveRecording = item.recording === true;
  // An item with no path has nothing to ask the content server for — straight to the placeholder,
  // rather than a request that is guaranteed to fail. A live recording's file is still growing, so
  // its frame would be a mid-write half-shot; the card advertises the capture instead.
  const showThumbnail = thumbnailLoadingActive && !missingVideo && !highlightsOnly && !liveRecording && item.filePath.length > 0;
  const preview = previewHighlights.slice(0, 3);
  // A live capture is not playable yet. It opens only to its own highlights — and only when it
  // actually has some, so the card itself does the "cannot interact" part of the contract.
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

      {/* A session being written cannot be deleted out from under the recorder. */}
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
