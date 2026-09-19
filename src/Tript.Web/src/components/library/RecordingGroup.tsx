// SPDX-License-Identifier: GPL-2.0-or-later

import type { ContentItem } from '../../ipc/protocol';
import { linkedAutomaticHighlights, type RecordingGroup as Group } from './libraryModel';
import { ContentCard } from './ContentCard';

export interface GroupActions {
  onOpen?: (item: ContentItem) => void;
  onDelete?: (item: ContentItem) => void;
  onToggleFavorite?: (item: ContentItem) => void;
  selectable?: boolean;
  isSelected?: (item: ContentItem) => boolean;
  onToggleSelected?: (item: ContentItem) => void;
}

interface CardOptions {
  variant?: 'grid' | 'wide';
  priority?: boolean;
  thumbnailLoadingActive?: boolean;
  clipsCount?: number;
  highlightsCount?: number;
  previewHighlights?: ContentItem[];
  sizeBytes?: number;
}

function cardFor(item: ContentItem, actions: GroupActions, options: CardOptions = {}) {
  const {
    variant = 'grid',
    priority = false,
    thumbnailLoadingActive = true,
    clipsCount = 0,
    highlightsCount = 0,
    previewHighlights = [],
    sizeBytes,
  } = options;

  return (
    <ContentCard
      item={item}
      variant={variant}
      priority={priority}
      thumbnailLoadingActive={thumbnailLoadingActive}
      clipsCount={clipsCount}
      highlightsCount={highlightsCount}
      previewHighlights={previewHighlights}
      sizeBytes={sizeBytes}
      onOpen={actions.onOpen}
      onDelete={actions.onDelete}
      onToggleFavorite={actions.onToggleFavorite}
      selectable={actions.selectable}
      selected={actions.isSelected?.(item) ?? false}
      onToggleSelected={actions.onToggleSelected}
    />
  );
}

export function RecordingGroup({
  group,
  variant,
  actions,
  thumbnailLoadingActive = true,
  priority = false,
  sizeBytes,
}: {
  group: Group;
  variant: 'hero' | 'recent' | 'row';
  actions: GroupActions;
  thumbnailLoadingActive?: boolean;
  priority?: boolean;
  sizeBytes?: number;
}) {
  const recording = group.recording;
  const automaticClips = recording ? linkedAutomaticHighlights(recording, group.clips) : [];
  const shared: CardOptions = {
    thumbnailLoadingActive,
    clipsCount: group.clips.length,
    highlightsCount: automaticClips.length,
    previewHighlights: automaticClips,
    sizeBytes,
  };

  if ((variant === 'hero' || variant === 'recent') && recording) {
    return (
      <section
        className={variant === 'recent' ? 'recording-group recording-group--recent' : 'library-hero'}
        data-testid={variant === 'recent' ? 'recording-group-recent' : 'library-hero'}
        aria-label={variant === 'recent' ? 'Recent recording' : 'Newest recording'}
      >
        {cardFor(recording, actions, {
          ...shared,
          variant: variant === 'recent' ? 'grid' : 'wide',
          priority,
        })}
      </section>
    );
  }

  if (!recording) {
    return null;
  }

  return (
    <section className="recording-group" data-testid="recording-group">
      <div className="recording-group-body">
        <div className="recording-group-head">{cardFor(recording, actions, shared)}</div>
      </div>
    </section>
  );
}
