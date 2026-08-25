// SPDX-License-Identifier: GPL-2.0-or-later

import type { ContentItem } from '../../ipc/protocol';
import { Button } from '../ui/controls';

interface PlayerHeaderProps {
  item: ContentItem;
  creatingHighlights: boolean;
  highlightsPaused: boolean;
  highlightCount: number;
  onBack?: () => void;
  onAutomaticClips: () => void;
  onDelete?: (item: ContentItem) => void;
  onReviewSession?: (recording: ContentItem) => void;
  convertHdrClipsToSdr?: boolean;
  recording?: boolean;
  convertingToSdr?: boolean;
  onConvertToSdr?: () => void;
  conversionError?: string | null;
}

export function PlayerHeader({
  item,
  creatingHighlights,
  highlightsPaused,
  highlightCount,
  onBack,
  onAutomaticClips,
  onDelete,
  onReviewSession,
  convertHdrClipsToSdr = false,
  recording = false,
  convertingToSdr = false,
  onConvertToSdr,
  conversionError,
}: PlayerHeaderProps) {
  const canConvertToSdr = convertHdrClipsToSdr && item.isHdr === true
    && (item.contentType === 'clip' || item.contentType === 'highlight');

  return (
    <div className="player-header">
      <Button variant="ghost" size="small" icon="chevronLeft" onClick={onBack}>
        Back
      </Button>
      <span className="player-header-title">{item.title?.trim() || item.fileName}</span>
      {canConvertToSdr && (
        <Button
          variant="ghost"
          size="small"
          onClick={() => onConvertToSdr?.()}
          disabled={recording || convertingToSdr}
          title={recording ? 'SDR conversion is unavailable while recording' : 'Create an SDR copy of this HDR clip'}
        >
          {convertingToSdr ? 'Creating SDR...' : 'Create SDR version'}
        </Button>
      )}
      {canConvertToSdr && conversionError && (
        <span className="player-header-error" role="alert">{conversionError}</span>
      )}
      {item.contentType === 'recording' && (
        <Button variant="ghost" size="small" onClick={onAutomaticClips} disabled={recording}>
          {creatingHighlights ? (highlightsPaused ? 'Resume highlights' : 'Pause highlights') : 'Create highlights'}
        </Button>
      )}
      {item.contentType === 'recording' && highlightCount > 0 && onReviewSession && (
        <Button variant="ghost" size="small" onClick={() => onReviewSession(item)}>
          View highlights ({highlightCount})
        </Button>
      )}
      {item.automated && onDelete && (
        <Button
          variant="ghost"
          size="icon"
          className="player-header-delete"
          icon="trash"
          onClick={() => onDelete(item)}
          aria-label="Move highlight to trash"
          title="Move highlight to trash"
        />
      )}
    </div>
  );
}
