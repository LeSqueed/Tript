// SPDX-License-Identifier: GPL-2.0-or-later

import type { ContentItem } from '../../ipc/protocol';
import { useEffect, useRef, useState } from 'react';
import { Button, TextField } from '../ui/controls';

interface PlayerHeaderProps {
  item: ContentItem;
  reviewRecording?: ContentItem;
  creatingHighlights: boolean;
  highlightsPaused: boolean;
  highlightCount: number;
  /**
   * Whether the recording has at least one detected event automatic highlights could cut. When
   * false, the "Create highlights" action is disabled: asking the backend to cut nothing would
   * only produce an error the button could have avoided.
   */
  canCreateHighlights: boolean;
  onBack?: () => void;
  onAutomaticClips: () => void;
  onRename?: (item: ContentItem, title: string) => void;
  onOpenFileLocation?: (item: ContentItem) => void;
  onReviewSession?: (recording: ContentItem) => void;
  convertHdrClipsToSdr?: boolean;
  recording?: boolean;
  convertingToSdr?: boolean;
  onConvertToSdr?: () => void;
  conversionError?: string | null;
}

export function PlayerHeader({
  item,
  reviewRecording,
  creatingHighlights,
  highlightsPaused,
  highlightCount,
  canCreateHighlights,
  onBack,
  onAutomaticClips,
  onRename,
  onOpenFileLocation,
  onReviewSession,
  convertHdrClipsToSdr = false,
  recording = false,
  convertingToSdr = false,
  onConvertToSdr,
  conversionError,
}: PlayerHeaderProps) {
  const cancellingRename = useRef(false);
  const displayTitle = item.title?.trim() || item.fileName;
  const [editingTitle, setEditingTitle] = useState(false);
  const [title, setTitle] = useState(displayTitle);
  const canConvertToSdr = convertHdrClipsToSdr && item.isHdr === true
    && (item.contentType === 'clip' || item.contentType === 'highlight');

  useEffect(() => {
    setEditingTitle(false);
    setTitle(displayTitle);
  }, [item.filePath, displayTitle]);

  const commitRename = () => {
    setEditingTitle(false);
    const nextTitle = title.trim();
    if (!cancellingRename.current && nextTitle !== displayTitle) {
      onRename?.(item, nextTitle);
    }
    cancellingRename.current = false;
  };

  return (
    <div className="player-header">
      <Button variant="ghost" size="small" icon="chevronLeft" onClick={onBack}>
        Back
      </Button>
      <div className="player-header-heading">
        {editingTitle ? (
          <TextField
            className="player-header-title-input"
            value={title}
            onChange={setTitle}
            autoFocus
            aria-label="Video title"
            onFocus={(event) => event.currentTarget.select()}
            onBlur={commitRename}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                event.currentTarget.blur();
              } else if (event.key === 'Escape') {
                cancellingRename.current = true;
                setTitle(displayTitle);
                event.currentTarget.blur();
              }
            }}
          />
        ) : (
          <span className="player-header-title" title={displayTitle}>{displayTitle}</span>
        )}
        {onRename && !editingTitle && (
          <Button
            variant="ghost"
            size="icon"
            icon="pencil"
            className="player-header-title-action"
            onClick={() => {
              cancellingRename.current = false;
              setTitle(displayTitle);
              setEditingTitle(true);
            }}
            aria-label="Rename video"
            title="Rename video"
          />
        )}
        {onOpenFileLocation && (
          <Button
            variant="ghost"
            size="icon"
            icon="folder"
            className="player-header-title-action"
            onClick={() => onOpenFileLocation(item)}
            aria-label="Show video in folder"
            title="Show video in folder"
          />
        )}
      </div>
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
        <Button variant="ghost" size="small" onClick={onAutomaticClips}
          disabled={recording || (!creatingHighlights && !canCreateHighlights)}
          title={!creatingHighlights && !canCreateHighlights
            ? 'This recording has no detected events to cut highlights from'
            : undefined}>
          {creatingHighlights ? (highlightsPaused ? 'Resume highlights' : 'Pause highlights') : 'Create highlights'}
        </Button>
      )}
      {reviewRecording && highlightCount > 0 && onReviewSession && (
        <Button variant="ghost" size="small" onClick={() => onReviewSession(reviewRecording)}>
          Review highlights ({highlightCount})
        </Button>
      )}
    </div>
  );
}
